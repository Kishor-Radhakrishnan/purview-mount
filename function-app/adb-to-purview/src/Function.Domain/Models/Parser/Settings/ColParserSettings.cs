// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using Function.Domain.Models.Purview;

namespace Function.Domain.Models.Settings
{
    public class ColParserSettings
    {
        public List<OlToPurviewMapping> OlToPurviewMappings = new List<OlToPurviewMapping>();
    }
}