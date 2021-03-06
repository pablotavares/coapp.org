//-----------------------------------------------------------------------
// <copyright company="CoApp Project">
//     Copyright (c) 2010-2012 Garrett Serack and CoApp Contributors. 
//     Contributors can be discovered using the 'git log' command.
//     All rights reserved.
// </copyright>
// <license>
//     The software is licensed under the Apache 2.0 License (the "License")
//     You may not use the software except in compliance with the License. 
// </license>
//-----------------------------------------------------------------------

namespace Handlers {
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using System.Web;
    using Extensions;
    using Newtonsoft.Json.Linq;
    using Services;
    using CoApp.Toolkit.Collections;
    using CoApp.Toolkit.Logging;
    using CoApp.Toolkit.Pipes;
    // using CoApp.Toolkit.Tasks;
    using Microsoft.WindowsAzure;

    public class CommitMessageHandler : RequestHandler {
        private Tweeter _tweeter;
        private readonly IDictionary<string, string> _aliases = new XDictionary<string,string>();
        private bool _initialized;

        public override void LoadSettings(HttpContext context) {
            lock (this) {
                if (!_initialized) {
                    var twitterHandle = CloudConfigurationManager.GetSetting("tweet-commits");
                    if (!string.IsNullOrEmpty(twitterHandle)) {
                        _tweeter = new Tweeter(twitterHandle);
                    }

                    var aliases = CloudConfigurationManager.GetSetting("github-aliases");

                    foreach (var set in aliases.Split(new[] {',', ' '}, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Split('=')).Where(set => set.Length == 2)) {
                        _aliases.Add( set[0],set[1]);
                    }

                    _initialized = true;
                }
            }
        }

        public override void Get(HttpResponse response, string relativePath, UrlEncodedMessage message) {
            response.WriteString("<html><body>Relative Path: {0}<br>GET : <br>", relativePath);
            foreach( var key in message ) {
                response.WriteString("&nbsp;&nbsp;&nbsp;{0} = {1}<br>", key, message[key]);
            }

            Bus.SendRegenerateSiteMessage();
            response.WriteString("</body></html>");
        }

        public override void Post(HttpResponse response, string relativePath, UrlEncodedMessage message) {
            var payload = message["payload"];
            if( payload == null ) {
                response.StatusCode = 500;
                response.Close();
                return;
            }
            Logger.Message("payload = {0}",payload);
                try {
                    dynamic json = JObject.Parse(payload);
                    Logger.Message("MSG Process begin {0}", json.commits.Count);
                    
                    var count = json.commits.Count;
                    var doSiteRebuild = false;
                    for (int i = 0; i < count; i++) {
                        string username = json.commits[i].author.email.Value;
                        var atSym = username.IndexOf('@');
                        if( atSym > -1 ) {
                            username = username.Substring(0, atSym);
                        }

                        var commitMessage = json.commits[i].message.Value;
                        var repository = json.repository.name.Value;
                        
                        var url = (string)json.commits[i].url.Value;
                        if (repository == "coapp.org") {
                            doSiteRebuild = true;
                        }

                        Bitly.Shorten(url).ContinueWith( (bitlyAntecedent) => {
                            var commitUrl = bitlyAntecedent.Result;

                            var handle = _aliases.ContainsKey(username) ? _aliases[username] : username;
                            var sz = repository.Length + handle.Length + commitUrl.Length + commitMessage.Length + 10;
                            var n = 140 - sz;

                            if (n < 0) {
                                commitMessage = commitMessage.Substring(0, (commitMessage.Length + n) - 1) + "\u2026";
                            }

                            _tweeter.Tweet("{0} => {1} via {2} {3}", repository, commitMessage, handle, commitUrl);
                            Logger.Message("{0} => {1} via {2} {3}", repository, commitMessage, handle, commitUrl);
                        });
                    }
                    // just rebuild the site once for a given batch of rebuild commit messages.
                    if( doSiteRebuild) {
                        Task.Factory.StartNew(() => {
                            try {
                                Logger.Message("Rebuilding website.");
                                Bus.SendRegenerateSiteMessage();

                            } catch( Exception e ) {
                                HandleException(e);
                            }
                        });
                    }    
                } catch(Exception e) {
                    Logger.Error("Error handling uploaded package: {0} -- {1}\r\n{2}", e.GetType(), e.Message, e.StackTrace);
                    HandleException(e);
                    response.StatusCode = 500;
                    response.Close();
                }
         }
    }
}