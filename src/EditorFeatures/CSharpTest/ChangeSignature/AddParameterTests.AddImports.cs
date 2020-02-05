﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.ChangeSignature;
using Microsoft.CodeAnalysis.Editor.UnitTests.ChangeSignature;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.CodeAnalysis.Test.Utilities.ChangeSignature;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.Editor.CSharp.UnitTests.ChangeSignature
{
    public partial class ChangeSignatureTests : AbstractChangeSignatureTests
    {
        [WpfFact, Trait(Traits.Feature, Traits.Features.ChangeSignature)]
        public async Task AddParameterAddsAllImports()
        {
            var markup = @"
class C
{
    void $$M() { }
}";

            var updatedSignature = new[] {
                new AddedParameterOrExistingIndex(
                    new AddedParameter(
                        null,
                        "Dictionary<ConsoleColor, Task<AsyncOperation>>",
                        "test",
                        "TODO"),
                    "System.Collections.Generic.Dictionary<System.ConsoleColor, System.Threading.Tasks.Task<System.ComponentModel.AsyncOperation>>")};

            var updatedCode = @"
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;

class C
{
    void M(Dictionary<ConsoleColor, Task<AsyncOperation>> test) { }
}";

            await TestChangeSignatureViaCommandAsync(LanguageNames.CSharp, markup, updatedSignature: updatedSignature, expectedUpdatedInvocationDocumentCode: updatedCode);
        }

        [WpfFact, Trait(Traits.Feature, Traits.Features.ChangeSignature)]
        public async Task AddParameterAddsOnlyMissingImports()
        {
            var markup = @"
using System.ComponentModel;

class C
{
    void $$M() { }
}";

            var updatedSignature = new[] {
                new AddedParameterOrExistingIndex(
                    new AddedParameter(
                        null,
                        "Dictionary<ConsoleColor, Task<AsyncOperation>>",
                        "test",
                        "TODO"),
                    "System.Collections.Generic.Dictionary<System.ConsoleColor, System.Threading.Tasks.Task<System.ComponentModel.AsyncOperation>>")};

            var updatedCode = @"
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;

class C
{
    void M(Dictionary<ConsoleColor, Task<AsyncOperation>> test) { }
}";

            await TestChangeSignatureViaCommandAsync(LanguageNames.CSharp, markup, updatedSignature: updatedSignature, expectedUpdatedInvocationDocumentCode: updatedCode);
        }

    }
}
