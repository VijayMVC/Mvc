// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Razor.TagHelpers;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.TagHelpers.Internal;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Caching.Memory;

namespace Microsoft.AspNetCore.Mvc.TagHelpers
{
    /// <summary>
    /// <see cref="ITagHelper"/> implementation targeting &lt;script&gt; elements that supports fallback src paths.
    /// </summary>
    /// <remarks>
    /// The tag helper won't process for cases with just the 'src' attribute.
    /// </remarks>
    [HtmlTargetElement("script", Attributes = SrcIncludeAttributeName)]
    [HtmlTargetElement("script", Attributes = SrcExcludeAttributeName)]
    [HtmlTargetElement("script", Attributes = FallbackSrcAttributeName)]
    [HtmlTargetElement("script", Attributes = FallbackSrcIncludeAttributeName)]
    [HtmlTargetElement("script", Attributes = FallbackSrcExcludeAttributeName)]
    [HtmlTargetElement("script", Attributes = FallbackTestExpressionAttributeName)]
    [HtmlTargetElement("script", Attributes = AppendVersionAttributeName)]
    public class ScriptTagHelper : UrlResolutionTagHelper
    {
        private const string SrcIncludeAttributeName = "asp-src-include";
        private const string SrcExcludeAttributeName = "asp-src-exclude";
        private const string FallbackSrcAttributeName = "asp-fallback-src";
        private const string FallbackSrcIncludeAttributeName = "asp-fallback-src-include";
        private const string FallbackSrcExcludeAttributeName = "asp-fallback-src-exclude";
        private const string FallbackTestExpressionAttributeName = "asp-fallback-test";
        private const string SrcAttributeName = "src";
        private const string AppendVersionAttributeName = "asp-append-version";
        private static readonly Func<Mode, Mode, int> Compare = (a, b) => a - b;
        private FileVersionProvider _fileVersionProvider;
        private StringWriter _stringWriter;

        private static readonly ModeAttributes<Mode>[] ModeDetails = new[] {
            // Regular src with file version alone
            new ModeAttributes<Mode>(Mode.AppendVersion, new[] { AppendVersionAttributeName }),
            // Globbed src (include only)
            new ModeAttributes<Mode>(Mode.GlobbedSrc, new [] { SrcIncludeAttributeName }),
            // Globbed src (include & exclude)
            new ModeAttributes<Mode>(Mode.GlobbedSrc, new [] { SrcIncludeAttributeName, SrcExcludeAttributeName }),
            // Fallback with static src
            new ModeAttributes<Mode>(Mode.Fallback,
                new[]
                {
                    FallbackSrcAttributeName,
                    FallbackTestExpressionAttributeName
                }),
            // Fallback with globbed src (include only)
            new ModeAttributes<Mode>(
                Mode.Fallback,
                new[]
                {
                    FallbackSrcIncludeAttributeName,
                    FallbackTestExpressionAttributeName
                }),
            // Fallback with globbed src (include & exclude)
            new ModeAttributes<Mode>(
                Mode.Fallback,
                new[]
                {
                    FallbackSrcIncludeAttributeName,
                    FallbackSrcExcludeAttributeName,
                    FallbackTestExpressionAttributeName
                }),
        };

        /// <summary>
        /// Creates a new <see cref="ScriptTagHelper"/>.
        /// </summary>
        /// <param name="hostingEnvironment">The <see cref="IHostingEnvironment"/>.</param>
        /// <param name="cache">The <see cref="IMemoryCache"/>.</param>
        /// <param name="htmlEncoder">The <see cref="HtmlEncoder"/>.</param>
        /// <param name="javaScriptEncoder">The <see cref="JavaScriptEncoder"/>.</param>
        /// <param name="urlHelperFactory">The <see cref="IUrlHelperFactory"/>.</param>
        public ScriptTagHelper(
            IHostingEnvironment hostingEnvironment,
            IMemoryCache cache,
            HtmlEncoder htmlEncoder,
            JavaScriptEncoder javaScriptEncoder,
            IUrlHelperFactory urlHelperFactory)
            : base(urlHelperFactory, htmlEncoder)
        {
            HostingEnvironment = hostingEnvironment;
            Cache = cache;
            JavaScriptEncoder = javaScriptEncoder;
        }

        /// <inheritdoc />
        public override int Order
        {
            get
            {
                return -1000;
            }
        }

        /// <summary>
        /// Address of the external script to use.
        /// </summary>
        /// <remarks>
        /// Passed through to the generated HTML in all cases.
        /// </remarks>
        [HtmlAttributeName(SrcAttributeName)]
        public string Src { get; set; }

        /// <summary>
        /// A comma separated list of globbed file patterns of JavaScript scripts to load.
        /// The glob patterns are assessed relative to the application's 'webroot' setting.
        /// </summary>
        [HtmlAttributeName(SrcIncludeAttributeName)]
        public string SrcInclude { get; set; }

        /// <summary>
        /// A comma separated list of globbed file patterns of JavaScript scripts to exclude from loading.
        /// The glob patterns are assessed relative to the application's 'webroot' setting.
        /// Must be used in conjunction with <see cref="SrcInclude"/>.
        /// </summary>
        [HtmlAttributeName(SrcExcludeAttributeName)]
        public string SrcExclude { get; set; }

        /// <summary>
        /// The URL of a Script tag to fallback to in the case the primary one fails.
        /// </summary>
        [HtmlAttributeName(FallbackSrcAttributeName)]
        public string FallbackSrc { get; set; }

        /// <summary>
        /// Value indicating if file version should be appended to src urls.
        /// </summary>
        /// <remarks>
        /// A query string "v" with the encoded content of the file is added.
        /// </remarks>
        [HtmlAttributeName(AppendVersionAttributeName)]
        public bool? AppendVersion { get; set; }

        /// <summary>
        /// A comma separated list of globbed file patterns of JavaScript scripts to fallback to in the case the
        /// primary one fails.
        /// The glob patterns are assessed relative to the application's 'webroot' setting.
        /// </summary>
        [HtmlAttributeName(FallbackSrcIncludeAttributeName)]
        public string FallbackSrcInclude { get; set; }

        /// <summary>
        /// A comma separated list of globbed file patterns of JavaScript scripts to exclude from the fallback list, in
        /// the case the primary one fails.
        /// The glob patterns are assessed relative to the application's 'webroot' setting.
        /// Must be used in conjunction with <see cref="FallbackSrcInclude"/>.
        /// </summary>
        [HtmlAttributeName(FallbackSrcExcludeAttributeName)]
        public string FallbackSrcExclude { get; set; }

        /// <summary>
        /// The script method defined in the primary script to use for the fallback test.
        /// </summary>
        [HtmlAttributeName(FallbackTestExpressionAttributeName)]
        public string FallbackTestExpression { get; set; }

        protected IHostingEnvironment HostingEnvironment { get; }

        protected IMemoryCache Cache { get; }

        protected JavaScriptEncoder JavaScriptEncoder { get; }

        // Internal for ease of use when testing.
        protected internal GlobbingUrlBuilder GlobbingUrlBuilder { get; set; }

        // Shared writer for determining the string content of a TagHelperAttribute's Value.
        private StringWriter StringWriter
        {
            get
            {
                if (_stringWriter == null)
                {
                    _stringWriter = new StringWriter();
                }

                return _stringWriter;
            }
        }

        /// <inheritdoc />
        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (output == null)
            {
                throw new ArgumentNullException(nameof(output));
            }

            // Pass through attribute that is also a well-known HTML attribute.
            if (Src != null)
            {
                output.CopyHtmlAttribute(SrcAttributeName, context);
            }

            // If there's no "src" attribute in output.Attributes this will noop.
            ProcessUrlAttribute(SrcAttributeName, output);

            // Retrieve the TagHelperOutput variation of the "src" attribute in case other TagHelpers in the
            // pipeline have touched the value. If the value is already encoded this ScriptTagHelper may
            // not function properly.
            Src = output.Attributes[SrcAttributeName]?.Value as string;

            Mode mode;
            if (!AttributeMatcher.TryDetermineMode(context, ModeDetails, Compare, out mode))
            {
                // No attributes matched so we have nothing to do
                return;
            }

            if (AppendVersion == true)
            {
                EnsureFileVersionProvider();

                if (Src != null)
                {
                    var index = output.Attributes.IndexOfName(SrcAttributeName);
                    output.Attributes[index] = new TagHelperAttribute(
                        SrcAttributeName,
                        _fileVersionProvider.AddFileVersionToPath(Src));
                }
            }

            var builder = new DefaultTagHelperContent();

            if (mode == Mode.GlobbedSrc || mode == Mode.Fallback && !string.IsNullOrEmpty(SrcInclude))
            {
                BuildGlobbedScriptTags(output.Attributes, builder);
                if (string.IsNullOrEmpty(Src))
                {
                    // Only SrcInclude is specified. Don't render the original tag.
                    output.TagName = null;
                    output.Content.SetContent(string.Empty);
                }
            }

            if (mode == Mode.Fallback)
            {
                string resolvedUrl;
                if (TryResolveUrl(FallbackSrc, resolvedUrl: out resolvedUrl))
                {
                    FallbackSrc = resolvedUrl;
                }

                BuildFallbackBlock(output.Attributes, builder);
            }

            output.PostElement.SetHtmlContent(builder);
        }

        private void BuildGlobbedScriptTags(
            TagHelperAttributeList attributes,
            TagHelperContent builder)
        {
            EnsureGlobbingUrlBuilder();

            // Build a <script> tag for each matched src as well as the original one in the source file
            var urls = GlobbingUrlBuilder.BuildUrlList(null, SrcInclude, SrcExclude);
            foreach (var url in urls)
            {
                // "url" values come from bound attributes and globbing. Must always be non-null.
                Debug.Assert(url != null);

                if (string.Equals(url, Src, StringComparison.OrdinalIgnoreCase))
                {
                    // Don't build duplicate script tag for the original source url.
                    continue;
                }

                BuildScriptTag(url, attributes, builder);
            }
        }

        private void BuildFallbackBlock(TagHelperAttributeList attributes, DefaultTagHelperContent builder)
        {
            EnsureGlobbingUrlBuilder();

            var fallbackSrcs = GlobbingUrlBuilder.BuildUrlList(FallbackSrc, FallbackSrcInclude, FallbackSrcExclude);
            if (fallbackSrcs.Count > 0)
            {
                // Build the <script> tag that checks the test method and if it fails, renders the extra script.
                builder.AppendHtml(Environment.NewLine)
                       .AppendHtml("<script>(")
                       .AppendHtml(FallbackTestExpression)
                       .AppendHtml("||document.write(\"");

                foreach (var src in fallbackSrcs)
                {
                    // Fallback "src" values come from bound attributes and globbing. Must always be non-null.
                    Debug.Assert(src != null);

                    builder.AppendHtml("<script");

                    var addSrc = true;

                    // Perf: Avoid allocating enumerator
                    for (var i = 0; i < attributes.Count; i++)
                    {
                        var attribute = attributes[i];
                        if (!attribute.Name.Equals(SrcAttributeName, StringComparison.OrdinalIgnoreCase))
                        {
                            var encodedKey = JavaScriptEncoder.Encode(attribute.Name);
                            var attributeValue = GetAttributeValue(attribute.Value);
                            var encodedValue = JavaScriptEncoder.Encode(attributeValue);

                            AppendAttribute(builder, encodedKey, encodedValue, escapeQuotes: true);
                        }
                        else
                        {
                            addSrc = false;
                            AppendEncodedVersionedSrc(attribute.Name, src, builder, generateForDocumentWrite: true);
                        }
                    }

                    if (addSrc)
                    {
                        AppendEncodedVersionedSrc(SrcAttributeName, src, builder, generateForDocumentWrite: true);
                    }

                    builder.AppendHtml("><\\/script>");
                }

                builder.AppendHtml("\"));</script>");
            }
        }

        private string GetAttributeValue(object value)
        {
            string stringValue;
            var htmlEncodedString = value as HtmlEncodedString;
            if (htmlEncodedString != null)
            {
                // Value likely came from an HTML context in the .cshtml file but may still contain double quotes
                // since attribute could have been enclosed in single quotes.
                stringValue = htmlEncodedString.Value;
                stringValue = stringValue.Replace("\"", "&quot;");
            }
            else
            {
                var writer = StringWriter;
                RazorPage.WriteTo(writer, HtmlEncoder, value);

                // Value is now correctly HTML-encoded but may still contain double quotes since attribute could
                // have been enclosed in single quotes and portions that were HtmlEncodedStrings are not re-encoded.
                var builder = writer.GetStringBuilder();
                builder.Replace("\"", "&quot;");

                stringValue = builder.ToString();
                builder.Clear();
            }

            return stringValue;
        }

        private void AppendEncodedVersionedSrc(
            string srcName,
            string srcValue,
            TagHelperContent builder,
            bool generateForDocumentWrite)
        {
            if (AppendVersion == true)
            {
                srcValue = _fileVersionProvider.AddFileVersionToPath(srcValue);
            }

            if (generateForDocumentWrite)
            {
                // srcValue comes from a C# context and globbing. Must HTML-encode it to ensure the
                // written <script/> element is valid. Must also JavaScript-encode that value to ensure
                // the document.write() statement is valid.
                srcValue = HtmlEncoder.Encode(srcValue);
                srcValue = JavaScriptEncoder.Encode(srcValue);
            }

            AppendAttribute(builder, srcName, srcValue, escapeQuotes: generateForDocumentWrite);
        }

        private void EnsureGlobbingUrlBuilder()
        {
            if (GlobbingUrlBuilder == null)
            {
                GlobbingUrlBuilder = new GlobbingUrlBuilder(
                    HostingEnvironment.WebRootFileProvider,
                    Cache,
                    ViewContext.HttpContext.Request.PathBase);
            }
        }

        private void EnsureFileVersionProvider()
        {
            if (_fileVersionProvider == null)
            {
                _fileVersionProvider = new FileVersionProvider(
                    HostingEnvironment.WebRootFileProvider,
                    Cache,
                    ViewContext.HttpContext.Request.PathBase);
            }
        }

        private void BuildScriptTag(
            string src,
            TagHelperAttributeList attributes,
            TagHelperContent builder)
        {
            builder.AppendHtml("<script");

            var addSrc = true;

            // Perf: Avoid allocating enumerator
            for (var i = 0; i < attributes.Count; i++)
            {
                var attribute = attributes[i];
                if (!attribute.Name.Equals(SrcAttributeName, StringComparison.OrdinalIgnoreCase))
                {
                    AppendAttribute(builder, attribute.Name, attribute.Value, escapeQuotes: false);
                }
                else
                {
                    addSrc = false;
                    AppendEncodedVersionedSrc(attribute.Name, src, builder, generateForDocumentWrite: false);
                }
            }

            if (addSrc)
            {
                AppendEncodedVersionedSrc(SrcAttributeName, src, builder, generateForDocumentWrite: false);
            }

            builder.AppendHtml("></script>");
        }

        private void AppendAttribute(TagHelperContent content, string key, object value, bool escapeQuotes)
        {
            content
                .AppendHtml(" ")
                .AppendHtml(key);
            if (escapeQuotes)
            {
                // Passed only JavaScript-encoded strings in this case. Do not perform HTML-encoding as well.
                content
                    .AppendHtml("=\\\"")
                    .AppendHtml((string)value)
                    .AppendHtml("\\\"");
            }
            else
            {
                // HTML-encode the given value if necessary.
                content
                    .AppendHtml("=\"")
                    .Append(HtmlEncoder, value)
                    .AppendHtml("\"");
            }
        }

        private enum Mode
        {
            /// <summary>
            /// Just adding a file version for the generated urls.
            /// </summary>
            AppendVersion = 0,

            /// <summary>
            /// Just performing file globbing search for the src, rendering a separate &lt;script&gt; for each match.
            /// </summary>
            GlobbedSrc = 1,

            /// <summary>
            /// Rendering a fallback block if primary javascript fails to load. Will also do globbing for both the
            /// primary and fallback srcs if the appropriate properties are set.
            /// </summary>
            Fallback = 2
        }
    }
}
