﻿// <copyright file="TranscriptionDefinition.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

namespace lasercat.models
{
    using System;
    using System.Collections.Generic;

    public sealed class TranscriptionDefinition
    {
        public TranscriptionDefinition() 
        {}
        
        public TranscriptionDefinition(string name, string description, string locale, Uri recordingsUrl)
        {
            this.Name = name;
            this.Description = description;
            this.RecordingsUrl = recordingsUrl;
            this.Locale = locale;
        }

        public TranscriptionDefinition(string name, string description, string locale, Uri recordingsUrl, IEnumerable<ModelIdentity> models)
        {
            this.Name = name;
            this.Description = description;
            this.RecordingsUrl = recordingsUrl;
            this.Locale = locale;
            this.Models = models;
        }

        /// <inheritdoc />
        public string Name { get; set; }

        /// <inheritdoc />
        public string Description { get; set; }

        /// <inheritdoc />
        public Uri RecordingsUrl { get; set; }

        public string Locale { get; set; }

        public IEnumerable<ModelIdentity> Models { get; set; }
    }
}
