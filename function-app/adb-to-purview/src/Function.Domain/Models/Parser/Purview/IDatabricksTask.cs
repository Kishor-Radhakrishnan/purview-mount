// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Newtonsoft.Json;
namespace Function.Domain.Models.Purview
{
    public interface IDatabricksTask
    {
        public string TypeName { get; set; }
        public  IDatabricksJobTaskAttributes Attributes { get; set; }
    }
}