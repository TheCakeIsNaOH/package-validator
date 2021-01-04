﻿// Copyright © 2015 - Present RealDimensions Software, LLC
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
//
// You may obtain a copy of the License at
//
// 	http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Net.Http;
using chocolatey.package.validator.infrastructure.app.configuration;

namespace chocolatey.package.validator.infrastructure.app.utility
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Linq;
    using System.Management.Automation;
    using System.Net;
    using System.Text.RegularExpressions;
    using System.Threading;
    using NuGet;

    public class Utility
    {
        private static readonly Regex _powerShellScriptRegex = new Regex(@"['""]?(\S+\.psm?1)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.IgnoreCase);

        private static readonly Regex _UrlRegex = new Regex(@"\b((?:https?://|www\.)\S+)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.IgnoreCase);

        public static IDictionary<IPackageFile, string> get_chocolatey_automation_scripts(IPackage package)
        {
            var files = package.GetFiles().Where(f => f.Path.to_lower().EndsWith(".ps1") || f.Path.to_lower().EndsWith(".psm1")).or_empty_list_if_null();
            var automationScripts = new Dictionary<IPackageFile, string>();

            foreach (var packageFile in files.or_empty_list_if_null())
            {
                var path = packageFile.Path.to_lower();
                if (path.EndsWith("chocolateyinstall.ps1") || path.EndsWith("chocolateybeforemodify.ps1") || path.EndsWith("chocolateyuninstall.ps1"))
                {
                    var contents = packageFile.GetStream().ReadToEnd();
                    automationScripts.Add(packageFile, contents);

                    // add any PowerShell scripts that were referenced
                    try
                    {
                        var matches = _powerShellScriptRegex.Matches(contents);
                        foreach (Match match in matches)
                        {
                            var scriptFileMatch = match.Groups[1].Value;
                            var scriptFile = files.FirstOrDefault(f => f.Path.to_lower().EndsWith(scriptFileMatch));
                            if (scriptFile != null && !automationScripts.ContainsKey(scriptFile))
                            {
                                automationScripts.Add(packageFile, scriptFile.GetStream().ReadToEnd());
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        "package-validator".Log().Warn("Error when searching for included powershell scripts - {0}".format_with(ex.Message));
                    }
                }
            }

            return automationScripts;
        }

        public static IDictionary<IPackageFile, ICollection<PSToken>> get_chocolatey_scripts_tokens(IPackage package)
        {
            var scriptsAsTokens = new Dictionary<IPackageFile, ICollection<PSToken>>();
            var scripts = get_chocolatey_automation_scripts(package);
            foreach (var script in scripts.or_empty_list_if_null())
            {
                Collection<PSParseError> errors = null;
                ICollection<PSToken> tokens = PSParser.Tokenize(script.Value, out errors);
                scriptsAsTokens.Add(script.Key, tokens);
            }

            return scriptsAsTokens;
        }

        public static bool package_has_binaries(IEnumerable<IPackageFile> packageFiles)
        {
            return packageFiles.Any(
                f =>
                    StringExtensions.to_lower(f.Path).EndsWith(".exe") ||
                    StringExtensions.to_lower(f.Path).EndsWith(".msi") ||
                    StringExtensions.to_lower(f.Path).EndsWith(".msu") ||
                    StringExtensions.to_lower(f.Path).EndsWith(".msp") ||
                    StringExtensions.to_lower(f.Path).EndsWith(".dll") ||
                    StringExtensions.to_lower(f.Path).EndsWith(".7z")  ||
                    StringExtensions.to_lower(f.Path).EndsWith(".zip") ||
                    StringExtensions.to_lower(f.Path).EndsWith(".gz")  ||
                    StringExtensions.to_lower(f.Path).EndsWith(".tar") ||
                    StringExtensions.to_lower(f.Path).EndsWith(".rar") ||
                    StringExtensions.to_lower(f.Path).EndsWith(".sfx") ||
                    StringExtensions.to_lower(f.Path).EndsWith(".iso") ||
                    StringExtensions.to_lower(f.Path).EndsWith(".dmg") ||
                    StringExtensions.to_lower(f.Path).EndsWith(".so")  ||
                    StringExtensions.to_lower(f.Path).EndsWith(".jar")
            );
        }

        /// <summary>
        ///   Tries to validate an URL
        /// </summary>
        /// <param name="url">Uri object</param>
        public static bool url_is_valid(Uri url)
        {
            return url_is_valid(url, string.Empty, string.Empty, string.Empty);
        }

        /// <summary>
        ///   Tries to validate an URL
        /// </summary>
        /// <param name="url">Uri object</param>
        /// <param name="proxyAddress">The web address to use when a proxy is required.</param>
        /// <param name="proxyUserName">The username to use when proxy requires authentication.</param>
        /// <param name="proxyPassword">The password to use when proxy requires authentication.</param>
        public static bool url_is_valid(Uri url, string proxyAddress, string proxyUserName, string proxyPassword)
        {
            if (url == null)
            {
                return true;
            }

            if (url.Scheme == "mailto")
            {
                // mailto links are not expected/allowed, therefore immediately fail with no further processing
                return false;
            }

            if (!url.Scheme.StartsWith("http"))
            {
                // Currently we can only validate http/https URL's, therefore simply return true for any others.
                return true;
            }

            try
            {
                var message = new HttpRequestMessage(HttpMethod.Get, url);

                System.Net.Http.HttpClient client = null;

                if (string.IsNullOrEmpty(proxyAddress))
                {
                    "package-validator".Log().Debug("Creating new HttpClient");
                    client = new System.Net.Http.HttpClient();
                }
                else
                {
                    HttpClientHandler httpClientHandler = null;
                    if (!string.IsNullOrEmpty(proxyUserName) && !string.IsNullOrEmpty(proxyPassword))
                    {
                        "package-validator".Log().Debug("Creating new HttpClient with authenticated proxy using web address: {0}", proxyAddress);
                        httpClientHandler = new HttpClientHandler
                        {
                            Proxy = new WebProxy(proxyAddress, BypassOnLocal: false, BypassList: null, Credentials: new NetworkCredential(proxyUserName, proxyPassword)),
                            UseProxy = true
                        };
                    }
                    else
                    {
                        "package-validator".Log().Debug("Creating new HttpClient with proxy using web address: {0}", proxyAddress);

                        httpClientHandler = new HttpClientHandler
                        {
                            Proxy = new WebProxy(proxyAddress, BypassOnLocal: false),
                            UseProxy = true
                        };
                    }

                    httpClientHandler.AllowAutoRedirect = true;

                    client = new System.Net.Http.HttpClient(httpClientHandler);
                }

                client.Timeout = TimeSpan.FromSeconds(30);

                client.DefaultRequestHeaders.Add("Connection", "keep-alive");

                message.Headers.Add("Upgrade-Insecure-Requests", "1");
                message.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 6.3; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/83.0.4103.116 Safari/537.36");
                message.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.9");
                message.Headers.Add("Sec-Fetch-Mode", "navigate");
                message.Headers.Add("Sec-Fetch-Dest", "document");
                message.Headers.Add("Sec-Fetch-Site", "cross-site");
                message.Headers.Add("Sec-Fetch-User", "?1");
                message.Headers.Add("Accept-Encoding", "gzip, deflate, br");
                message.Headers.Add("Accept-Language", "en-GB,en-US;q=0.8,en;q=0.6,de-DE;q=0.4,de;q=0.2");

                var response = client.SendAsync(message).GetAwaiter().GetResult();

                if (response.ReasonPhrase == "Permanent Redirect")
                {
                    "package-validator".Log().Warn("Received a HTTP Status Code of 308, which this version of .Net Framework HttpClient doesn't understand, assuming that this is a valid URL.");
                    "package-validator".Log().Warn("This check was put in place as a result of this issue: https://github.com/chocolatey/package-validator/issues/247");
                    return true;
                }

                if (response.StatusCode == HttpStatusCode.Forbidden && response.Headers.Server.ToString() == "cloudflare")
                {
                    "package-validator".Log().Warn("Received a Forbidden response validating Url {0}", url.ToString());
                    "package-validator".Log().Warn("Since this is likely due to the fact that the server is using Cloudflare, is sometimes popping up a Captcha which needs to be solved, obviously not possible by package-validator.");
                    "package-validator".Log().Warn("This check was put in place as a result of this issue: https://github.com/chocolatey/package-validator/issues/229");
                    return true;
                }

                if (response.StatusCode == HttpStatusCode.Redirect)
                {
                    "package-validator".Log().Warn("Received a 302 response (so assuming a valid URL) from the server when validating URL {0}", url.ToString());
                    "package-validator".Log().Warn("This check was put in place as a result of this issue: https://github.com/chocolatey/package-validator/issues/254");
                    return true;
                }

                return response.StatusCode == HttpStatusCode.OK;
            }
            catch (Exception ex)
            {
                var exceptions = new List<Exception>();
                var currentException = ex;
                exceptions.Add(currentException);

                while (currentException.InnerException != null)
                {
                    exceptions.Add(currentException.InnerException);
                    currentException = currentException.InnerException;
                }

                foreach(var exception in exceptions)
                {
                    if (exception.Message == "Unable to read data from the transport connection: An existing connection was forcibly closed by the remote host." ||
                        exception.Message == "An existing connection was forcibly closed by the remote host" ||
                        exception.Message == "The request was aborted: Could not create SSL/TLS secure channel.")
                    {
                        "package-validator".Log().Warn("Error validating Url {0} - {1}", url.ToString(), exception.Message);
                        "package-validator".Log().Warn("Since this is likely due to missing Ciphers on the machine hosting package-validator, this URL will be marked as valid for the time being.");
                        return true;
                    }
                }

                "package-validator".Log().Warn("Error validating Url {0} - {1}", url.ToString(), ex.Message);
                return false;
            }
        }

        /// <summary>
        ///   Fetches all URLs from a string, and validates if the url results in a 200 OK
        /// </summary>
        /// <param name="contents">String that might contain URL</param>
        public static bool all_urls_are_valid(String contents)
        {
            return all_urls_are_valid(contents, string.Empty, string.Empty, string.Empty);
        }

        /// <summary>
        ///   Fetches all URLs from a string, and validates if the url results in a 200 OK
        /// </summary>
        /// <param name="contents">String that might contain URL</param>
        /// <param name="proxyAddress">The web address to use when a proxy is required.</param>
        /// <param name="proxyUserName">The username to use when proxy requires authentication.</param>
        /// <param name="proxyPassword">The password to use when proxy requires authentication.</param>
        public static bool all_urls_are_valid(String contents, string proxyAddress, string proxyUserName, string proxyPassword)
        {
            var result = true;

            try
            {
                var matches = _UrlRegex.Matches(contents);
                foreach (Match match in matches)
                {
                    var url = match.Groups[1].Value;
                    var uri = new Uri(url);
                    var urlResult = url_is_valid(uri, proxyAddress, proxyUserName, proxyPassword);
                    if (!urlResult)
                    {
                        result = urlResult;
                    }

                    // This was added as a result of this issue: https://github.com/chocolatey/package-validator/issues/234
                    // It isn't pretty, but sending too many requests to GitHub in a row, and likely other sites, triggers
                    // 429 responses, which fails the validation process.  When looping through a collection of URL's, like
                    // those contained within a set of Release Notes for a package, add a delay between each check.
                    Thread.Sleep(1000);
                }
            }
            catch (Exception ex)
            {
                "package-validator".Log().Warn("Error when searching for urls - {0}".format_with(ex.Message));
                return false;
            }

            return result;
        }
    }
}
