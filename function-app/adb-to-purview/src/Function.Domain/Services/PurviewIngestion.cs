// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Logging;
using Function.Domain.Helpers;
using System.Net.Http;
using Function.Domain.Models;
using System.Text;
using System.Security.Cryptography;
using System.Runtime.Caching;
using Function.Domain.Models.Settings;
using Newtonsoft.Json;

namespace Function.Domain.Services
{

    /// <summary>
    /// Class responsible for interaction with Purview API
    /// </summary>
    public class PurviewIngestion : IPurviewIngestion
    {
        private PurviewClient _purviewClient;
        private Int64 initGuid = -1000;
        //flag use to mark if a data Asset is a Dummy type
        private Dictionary<string, PurviewCustomType> entitiesMarkedForDeletion = new Dictionary<string, PurviewCustomType>();
        private Dictionary<string, string> originalFqnToDiscoveredFqn = new Dictionary<string, string>();
        List<PurviewCustomType> inputs_outputs = new List<PurviewCustomType>();
        private JArray to_purview_Json = new JArray();
        private readonly ILogger<PurviewIngestion> _logger;
        private MemoryCache _cacheOfSeenEvents = MemoryCache.Default;
        private AppConfigurationSettings? config = new AppConfigurationSettings();
        private CacheItemPolicy cacheItemPolicy;
        /// <summary>
        /// Create Object
        /// </summary>
        /// <param name="log">logger object (ILogger<PurviewIngestion>)</param>
        public PurviewIngestion(ILogger<PurviewIngestion> log)
        {
            _logger = log;
            _purviewClient = new PurviewClient(_logger);
            log.LogInformation($"Got Purview Client!");
            cacheItemPolicy = new CacheItemPolicy
            {
                AbsoluteExpiration = DateTimeOffset.Now.AddSeconds(config.dataEntityCacheTimeInSeconds)
            };
        }

        /// <summary>
        /// Send to Microsoft Purview API an Array on Data Entities
        /// </summary>
        /// <param name="Processes">Array of Entities</param>
        /// <returns>Array on Entities</returns>
        public async Task<JArray> SendToPurview(JArray Processes, IColParser colParser)
        {
            foreach (JObject process in Processes)
            {

                if (await SendToPurview(process, colParser))
                {
                    return new JArray();
                }
            }
            return new JArray();
        }
        /// <summary>
        /// Send to Microsoft Purview API an single Entity to be inserted or updated
        /// </summary>
        /// <param name="json">Json Object</param>
        /// <returns>Boolean</returns>
        public async Task<bool> SendToPurview(JObject json, IColParser colParser)
        {
            var entitiesFromInitialJson = get_attribute("entities", json);

            if (entitiesFromInitialJson == null)
            {
                _logger.LogError("Not found Attribute entities on " + json.ToString());
                return false;
            }

            // This hash and cache helps to prevent processing the same event multiple times
            string ? dataEvent = CalculateHash(entitiesFromInitialJson.ToString());
            if (!_cacheOfSeenEvents.Contains(dataEvent))
            {
                var cacheItem = new CacheItem(dataEvent, dataEvent);  
                _cacheOfSeenEvents.Add(cacheItem, cacheItemPolicy);

                foreach (JObject purviewEntityToBeUpdated in entitiesFromInitialJson)
                {
                    if (IsProcessEntity(purviewEntityToBeUpdated))
                    {
                        JObject new_entity = await Validate_Process_Entities(purviewEntityToBeUpdated);
                        // Update Column mapping attribute based on the dictionary and inject the column parser with the openlineage event
                        // This lets us use the discovered inputs / outputs rather than just what open lineage provides.
                        string columnMapping = JsonConvert.SerializeObject(colParser.GetColIdentifiers(originalFqnToDiscoveredFqn));
                        new_entity["attributes"]!["columnMapping"] = columnMapping;
                        to_purview_Json.Add(new_entity);
                    }
                    else
                    {
                        if (EntityAttributesHaveBeenPopulated(purviewEntityToBeUpdated))
                        {
                            PurviewCustomType new_entity = await Validate_Entities(purviewEntityToBeUpdated);

                            if (purviewEntityToBeUpdated.ContainsKey("relationshipAttributes"))
                            {
                                // For every relationship attribute
                                foreach (var rel in purviewEntityToBeUpdated["relationshipAttributes"]!.Values<JProperty>())
                                {
                                    // If the relationship attribute has a qualified name property
                                    if (((JObject)(purviewEntityToBeUpdated["relationshipAttributes"]![rel!.Name]!)).ContainsKey("qualifiedName"))
                                    {
                                        string _qualifiedNameOfRelatedAsset = purviewEntityToBeUpdated["relationshipAttributes"]![rel!.Name]!["qualifiedName"]!.ToString();
                                        // If the entitiesMarkedForDeletion dictionary has this related asset
                                        // update the guid of the relationship attribute we're in to be the original one?
                                        if (this.entitiesMarkedForDeletion.ContainsKey(_qualifiedNameOfRelatedAsset))
                                        {
                                            purviewEntityToBeUpdated["relationshipAttributes"]![rel!.Name]!["guid"] = this.entitiesMarkedForDeletion[_qualifiedNameOfRelatedAsset].Properties["guid"];
                                        }
                                        else
                                        {
                                            // This entity is created solely to be able to search for the asset based on qualifiedName
                                            PurviewCustomType sourceEntity = new PurviewCustomType("search relationship"
                                                , ""
                                                , _qualifiedNameOfRelatedAsset
                                                , ""
                                                , "search relationship"
                                                , NewGuid() // This will be updated after successfully finding the asset via query in purview
                                                , _logger
                                                , _purviewClient);

                                            // TODO This should use the qualifiedNamePrefix filter
                                            // Currently fqn may change here
                                            QueryValeuModel sourceJson = await sourceEntity.QueryInPurview();

                                            // If the related asset has not been seen, add it to the list of assets to be deleted?
                                            if (!this.entitiesMarkedForDeletion.ContainsKey(_qualifiedNameOfRelatedAsset))
                                                this.entitiesMarkedForDeletion.Add(_qualifiedNameOfRelatedAsset, sourceEntity);
                                            // Update the guid of the relationship attribute with the one that was discovered ()
                                            // TODO Handle when sourceJson does not return a typed asset
                                            purviewEntityToBeUpdated["relationshipAttributes"]![rel!.Name]!["guid"] = sourceEntity.Properties["guid"];
                                        }

                                    }
                                }
                            }
                            to_purview_Json.Add(purviewEntityToBeUpdated);
                        }
                    }
                }

                HttpResponseMessage results;
                string? payload = "";

                if (inputs_outputs.Count > 0)
                {
                    JArray tempEntities = new JArray();
                    foreach (var newEntity in inputs_outputs)
                    {
                        if (newEntity.is_dummy_asset)
                        {
                            newEntity.Properties["attributes"]!["qualifiedName"] = newEntity.Properties["attributes"]!["qualifiedName"]!.ToString().ToLower();
                            tempEntities.Add(newEntity.Properties);
                        }
                    }
                    payload = "{\"entities\": " + tempEntities.ToString() + "}";
                    JObject? Jpayload = JObject.Parse(payload);
                    _logger.LogInformation($"Input/Output Entities to load: {Jpayload.ToString()}");
                    results = await _purviewClient.Send_to_Purview(payload);
                    if (results != null)
                    {
                        if (results.ReasonPhrase != "OK")
                        {
                            _logger.LogError($"Error Loading Input/Outputs to Purview: Return Code: {results.StatusCode} - Reason:{results.ReasonPhrase}");
                        }
                        else
                        {
                            var data = await results.Content.ReadAsStringAsync();
                            _logger.LogInformation($"Purview Loaded Relationship, Input and Output Entities: Return Code: {results.StatusCode} - Reason:{results.ReasonPhrase} - Content: {data}");
                        }
                    }
                    else
                    {
                        _logger.LogError($"Error Loading to Purview!");
                    }
                }
                if (to_purview_Json.Count > 0)
                {
                    _logger.LogDebug(to_purview_Json.ToString());
                    payload = "{\"entities\": " + to_purview_Json.ToString() + "}";
                    JObject? Jpayload = JObject.Parse(payload);
                    _logger.LogInformation($"To Purview Json Entities to load: {Jpayload.ToString()}");
                    results = await _purviewClient.Send_to_Purview(payload);
                    if (results != null)
                    {
                        if (results.ReasonPhrase != "OK")
                        {
                            _logger.LogError($"Error Loading to Purview JSON Entiitesto Purview: Return Code: {results.StatusCode} - Reason:{results.ReasonPhrase}");
                        }
                    }
                    else
                    {
                        _logger.LogError($"Error Loading to Purview!");
                    }
                    foreach (var deletableEntity in this.entitiesMarkedForDeletion)
                    {
                        await _purviewClient.Delete_Unused_Entity(deletableEntity.Key, "purview_custom_connector_generic_entity_with_columns");
                    }
                    return true;
                }
                else
                {
                    if (json.Count > 0)
                    {
                        _logger.LogInformation($"Payload: {json}");
                        _logger.LogError("Nothing found to load on to Purview, look if the payload is empty.");
                    }
                    else
                    {
                        _logger.LogError("No Purview entity to load");
                    }
                    foreach (var deletableEntity in this.entitiesMarkedForDeletion)
                    {
                        await _purviewClient.Delete_Unused_Entity(deletableEntity.Key, "purview_custom_connector_generic_entity_with_columns");
                    }
                    return false;
                }
            }
            _logger.LogInformation($"Payload already registered in Microsoft Purview: {json.ToString()}");
            return false;
        }
        private bool EntityAttributesHaveBeenPopulated(JObject questionableEntity)
        {
            if (!questionableEntity.ContainsKey("typeName"))
            {
                return false;
            }
            if (!questionableEntity.ContainsKey("attributes"))
            {
                return false;
            }
            if (questionableEntity["attributes"]!.GetType() != typeof(JObject))
                return false;

            if (!((JObject)questionableEntity["attributes"]!).ContainsKey("qualifiedName"))
            {
                return false;
            }
            return true;
        }
        private async Task<PurviewCustomType> Validate_Entities(JObject Process)
        {

            string qualifiedName = Process["attributes"]!["qualifiedName"]!.ToString();
            string Name = Process["attributes"]!["name"]!.ToString();
            string typename = Process["typeName"]!.ToString();
            //string guid = Process["guid"]!.ToString();
            PurviewCustomType sourceEntity = new PurviewCustomType(Name
                , typename
                , qualifiedName
                , typename
                , $"Data Assets {Name}"
                , NewGuid()
                , _logger
                , _purviewClient);


            QueryValeuModel sourceJson = await sourceEntity.QueryInPurview();
            // Capture the updated qualified name mapping in case column mapping needs it
            originalFqnToDiscoveredFqn[qualifiedName] = sourceEntity.currentQualifiedName();


            Process["guid"] = sourceEntity.Properties["guid"];

            String proctype = Process["typeName"]!.ToString();
            if (sourceEntity.Properties.ContainsKey("typeName")){
                String sourcetype = sourceEntity.Properties["typeName"]!.ToString();
                _logger.LogInformation($"PQN:{qualifiedName} Process Type name is {proctype} and sourceEntity original TypeName was {sourcetype}");
            }else{
                _logger.LogInformation($"PQN:{qualifiedName} Process Type name is {proctype} and sourceEntity original TypeName was not set");
            }

            if (sourceEntity.is_dummy_asset)
            {
                _logger.LogInformation("IN DUMMY ASSET AND ABOUT TO OVERWRITE");
                sourceEntity.Properties["typeName"] = Process["typeName"]!.ToString();
                if (!entitiesMarkedForDeletion.ContainsKey(qualifiedName))
                    entitiesMarkedForDeletion.Add(qualifiedName, sourceEntity);
                _logger.LogInformation($"Entity: {qualifiedName} Type: {typename}, Not found, Creating Dummy Entity");
                return sourceEntity;
            }
            if (!entitiesMarkedForDeletion.ContainsKey(qualifiedName))
                entitiesMarkedForDeletion.Add(qualifiedName, sourceEntity);
            return sourceEntity;
        }

        /// <summary>
        /// Transform the provided JSON object (an input or output entity for a Purview process).
        /// This entity will have their qualified name and type updated based on searching for
        /// an existing entity in the purview instance.
        /// In addition the entity is added to the inputs_outputs property of PurviewIngestion.
        /// </summary>
        /// <param name="outPutInput">Json Object</param>
        /// <param name="inorout">Should be either 'inputs' or 'outputs'</param>
        /// <returns>A PurviewCustomType</returns>
        private async Task<PurviewCustomType> SetOutputInput(JObject outPutInput, string inorout)
        {

            string qualifiedName = outPutInput["uniqueAttributes"]!["qualifiedName"]!.ToString();
            string newqualifiedName = qualifiedName;
            string[] tmpName = qualifiedName.Split('/');
            string Name = tmpName[tmpName.Length - 1];
            if (Name == "")
                Name = tmpName[tmpName.Length - 2];
            string typename = outPutInput["typeName"]!.ToString();
            string originalTypeName = typename;
            PurviewCustomType sourceEntity = new PurviewCustomType(Name
            , typename
            , qualifiedName
            , typename
            , $"Data Assets {Name}"
            , _purviewClient.NewGuid()
            , _logger
            , _purviewClient);

            QueryValeuModel sourceJson = await sourceEntity.QueryInPurview();
            // Capture the updated qualified name mapping in case column mapping needs it
            originalFqnToDiscoveredFqn[qualifiedName] = sourceEntity.currentQualifiedName();

            if (sourceEntity.is_dummy_asset)
            {
                outPutInput["typeName"] = sourceEntity.Properties["typeName"];
                outPutInput["uniqueAttributes"]!["qualifiedName"] = sourceEntity.Properties!["attributes"]!["qualifiedName"]!.ToString().ToLower();

                inputs_outputs.Add(sourceEntity);
                _logger.LogInformation($"{inorout} Entity: {qualifiedName} Type: {typename}, Not found, Creating Dummy Entity");
            }
            else
            {
                outPutInput["uniqueAttributes"]!["qualifiedName"] = sourceEntity.Properties!["attributes"]!["qualifiedName"]!.ToString();
                outPutInput["typeName"] = sourceEntity.Properties!["typeName"]!.ToString();
            }

            if (!entitiesMarkedForDeletion.ContainsKey(qualifiedName))
                entitiesMarkedForDeletion.Add(qualifiedName, sourceEntity);

            return sourceEntity;
        }
        private async Task<JObject> Validate_Process_Entities(JObject Process)
        {
            //Validate process
            string qualifiedName = Process["attributes"]!["qualifiedName"]!.ToString();
            string Name = Process["attributes"]!["name"]!.ToString(); ;
            string typename = Process["typeName"]!.ToString();
            PurviewCustomType processEntity = new PurviewCustomType(Name
                                , typename
                                , qualifiedName
                                , typename
                                , $"Data Assets {Name}"
                                , NewGuid()
                                , _logger
                                , _purviewClient);
            QueryValeuModel processModel = await processEntity.QueryInPurview();
            Process["guid"] = processEntity.Properties["guid"];
            Process["attributes"]!["qualifiedName"] = processEntity.Properties["attributes"]!["qualifiedName"]!.ToString();
            //Validate inputs
            foreach (JObject inputs in Process["attributes"]!["inputs"]!)
            {
                PurviewCustomType returnInput = await SetOutputInput(inputs!, "inputs");
            }
            //Validate Outputs
            foreach (JObject outputs in Process["attributes"]!["outputs"]!)
            {
                PurviewCustomType returnOutput = await SetOutputInput(outputs!, "outputs");
            }

            //Validate Relationships
            if (Process.ContainsKey("relationshipAttributes"))
            {
                foreach (var rel in Process["relationshipAttributes"]!.Values<JProperty>())
                {
                    qualifiedName = Process["relationshipAttributes"]![rel!.Name]!["qualifiedName"]!.ToString();
                    string[] tmpName = qualifiedName.Split('/');
                    Name = tmpName[tmpName.Length - 1];
                    typename = "purview_custom_connector_generic_entity_with_columns";
                    if (!entitiesMarkedForDeletion.ContainsKey(qualifiedName))
                    {

                        PurviewCustomType sourceEntity = new PurviewCustomType(Name
                            , typename
                            , qualifiedName
                            , typename
                            , $"Data Assets {Name}"
                            , NewGuid()
                            , _logger
                            , _purviewClient);


                        var outputObj = await sourceEntity.QueryInPurview();
                        // Capture the updated qualified name mapping in case column mapping needs it
                        originalFqnToDiscoveredFqn[qualifiedName] = sourceEntity.currentQualifiedName();

                        Process["relationshipAttributes"]![rel!.Name]!["guid"] = sourceEntity.Properties["guid"];
                        if (!entitiesMarkedForDeletion.ContainsKey(qualifiedName))
                            entitiesMarkedForDeletion.Add(qualifiedName, sourceEntity);
                    }
                    else
                    {
                        Process["relationshipAttributes"]![rel!.Name]!["guid"] = entitiesMarkedForDeletion[qualifiedName].Properties["guid"];
                    }
                }
            }
            return Process;
        }
        private bool IsProcessEntity(JObject Process)
        {
            var _typename = get_attribute("typeName", Process);
            if (_typename == null)
            {
                _logger.LogInformation("Not found Attribute typename on " + Process.ToString());
                return false;
            }
            var _attributes = get_attribute("attributes", Process);
            if (!_attributes.HasValues)
            {
                _logger.LogError("Not found Attribute attributes on " + Process.ToString());
                return false;
            }

            if (!((JObject)Process["attributes"]!).ContainsKey("columnMapping"))
            {
                _logger.LogInformation($"Not found Attribute columnMapping on {Process.ToString()} is not a Process Entity!");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Get Safe attributes in a Json object without needing to check if exists
        /// </summary>
        /// <param name="attribute_name">Name of the attribute</param>
        /// <param name="json_entity">Json Object</param>
        /// <returns>Attribute Value</returns>
        public JToken get_attribute(string attribute_name, JObject json_entity)
        {
            if (json_entity.SelectToken(attribute_name) != null)
            {
                return json_entity[attribute_name]!;
            }
            return new JObject();
        }

        // Method that looks over the section of the Json
        //that contain the relationship values (Entities and Columns)
        private Int64 NewGuid()
        {

            return initGuid--;
        }
        private static string CalculateHash(string payload)
        {
            var newKey = Encoding.UTF8.GetBytes(payload);

            var sha1 = SHA1.Create();
            sha1.Initialize();
            var result = sha1.ComputeHash(newKey);

            // if you replace this base64 version with one of the encoding 
            //   classes this will become corrupt due to nulls and other 
            //   control character values in the byte[]
            var outval = Convert.ToBase64String(result);

            return outval;
        }
    }

}