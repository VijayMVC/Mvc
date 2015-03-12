﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using Xunit;

namespace Microsoft.AspNet.Mvc.ModelBinding.Test
{
    public class ModelStateDictionaryTest
    {
        [Fact]
        public void Indexer_ThrowsOnNullKey()
        {
            var dictionary = new ModelStateDictionary();
            Assert.Throws<ArgumentNullException>(() => dictionary[null] = new ModelState());
        }
    }
}