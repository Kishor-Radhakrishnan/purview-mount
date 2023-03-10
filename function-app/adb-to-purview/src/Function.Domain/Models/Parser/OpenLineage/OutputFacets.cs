// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Function.Domain.Models.OL
{
    [JsonObject("facets")]
    public class OutputFacets
    {
        [JsonProperty("lifeCycleStateChange")]
        public LifeCycleStateChangeClass LifeCycleStateChange = new LifeCycleStateChangeClass();
        [JsonProperty("columnLineage")]
        public ColumnLineageFacetsClass ColFacets = new ColumnLineageFacetsClass();
    }

    public class LifeCycleStateChangeClass
    {
        [JsonProperty("lifeCycleStateChange")]
        public string LifeCycleStateChange = "";
    }
}