﻿/* Copyright (c) Citrix Systems, Inc. 
 * All rights reserved. 
 * 
 * Redistribution and use in source and binary forms, 
 * with or without modification, are permitted provided 
 * that the following conditions are met: 
 * 
 * *   Redistributions of source code must retain the above 
 *     copyright notice, this list of conditions and the 
 *     following disclaimer. 
 * *   Redistributions in binary form must reproduce the above 
 *     copyright notice, this list of conditions and the 
 *     following disclaimer in the documentation and/or other 
 *     materials provided with the distribution. 
 * 
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND 
 * CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, 
 * INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF 
 * MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE 
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR 
 * CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, 
 * SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, 
 * BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR 
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS 
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, 
 * WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING 
 * NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE 
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF 
 * SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Permissions;
using System.Windows.Forms;
using XenCenterLib;

namespace XenAdmin.Core
{
    /// <summary>
    /// This extension to System.Windows.Forms.WebBrowser adds two capabilities: an event fired whenever
    /// browser navigation fails (NavigateError), and an event fired whenever the browser wants to
    /// prompt for credentials (AuthenticationPrompt).
    /// 
    /// The event sink for NavigateError comes from
    /// http://msdn.microsoft.com/en-us/library/system.windows.forms.webbrowser.createsink(VS.80).aspx.
    /// The handling of authentication prompts comes partially from
    /// http://izlooite.blogspot.com/2009/06/bypass-integrated-authentication-using.html.
    /// The bit that he missed is that we can override CreateWebBrowserSiteBase to get ourselves
    /// registered rather than having to override IOleClientSite as well.  See 
    /// http://msdn2.microsoft.com/en-us/library/system.windows.forms.webbrowser.createwebbrowsersitebase.aspx.
    ///
    /// (Final note: Contrary to the docs, you can't use the CreateWebBrowserSiteBase override to reimplement
    /// IDocHostUIHandler.  This is an acknowledged Microsoft bug
    /// https://connect.microsoft.com/VisualStudio/feedback/ViewFeedback.aspx?FeedbackID=115198.
    /// Fortunately, we only need to implement IAuthenticate, not IDocHostUIHandler, so this isn't a problem
    /// for us, but it was confusing for a couple of days.)
    /// </summary>
    public class WebBrowser2 : WebBrowser
    {
        /// <summary>
        /// A list of <see cref="WebClient"/>s used to determine whether URLS are valid before Navigations.
        /// </summary>
        private readonly List<WebClient> _webClients = new List<WebClient>();
        private AxHost.ConnectionPointCookie cookie;
        private WebBrowser2EventHelper helper;


        public event WebBrowserNavigateErrorEventHandler NavigateError;

        public event EventHandler WindowClosed;

        public event WebBrowserAuthenticationPromptEventHandler AuthenticationPrompt;

        protected bool Authenticate(out string username, out string password)
        {
            if (AuthenticationPrompt == null)
            {
                username = "";
                password = "";
                return false;
            }
            else
            {
                WebBrowserAuthenticationPromptEventArgs args = new WebBrowserAuthenticationPromptEventArgs();
                AuthenticationPrompt(this, args);
                username = args.Username;
                password = args.Password;
                return args.Success;
            }
        }

        [PermissionSet(SecurityAction.LinkDemand, Name="FullTrust")]
        protected override void CreateSink()
        {
            base.CreateSink();

            // Create an instance of the client that will handle the event
            // and associate it with the underlying ActiveX control.
            helper = new WebBrowser2EventHelper(this);
            cookie = new AxHost.ConnectionPointCookie(
                this.ActiveXInstance, helper, typeof(DWebBrowserEvents2));
        }

        [PermissionSet(SecurityAction.LinkDemand, Name="FullTrust")]
        protected override void DetachSink()
        {
            // Disconnect the client that handles the event
            // from the underlying ActiveX control.
            if (cookie != null)
            {
                cookie.Disconnect();
                cookie = null;
            }
            base.DetachSink();
        }

        protected override WebBrowserSiteBase CreateWebBrowserSiteBase()
        {
            return new WebBrowserSite2(this);
        }

        protected virtual void OnNavigateError(WebBrowserNavigateErrorEventArgs e)
        {
            if (NavigateError != null)
                NavigateError(this, e);
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case Win32.WM_PARENTNOTIFY:
                    if (!DesignMode && (int)m.WParam == Win32.WM_DESTROY)
                    {
                        if (WindowClosed != null)
                            WindowClosed(this, EventArgs.Empty);
                    }
                    break;

                default:
                    base.WndProc(ref m);
                    break;
            }
        }

        protected override void OnNavigating(WebBrowserNavigatingEventArgs e)
        {
            Program.AssertOnEventThread();

            // clear the _webClients so that an existing multiple-url Navigation is cancelled.
            _webClients.Clear();
            base.OnNavigating(e);
        }

        /// <summary>
        /// Navigates to the specified URI.
        /// </summary>
        public new void Navigate(Uri uri)
        {
            Program.AssertOnEventThread();
            Navigate(uri, null, null, "X-XenCenter: " + Program.ClientVersion());
        }

        /// <summary>
        /// Navigates to the first valid URI in the specified list.
        /// </summary>
        public void Navigate(IEnumerable<Uri> uris)
        {
            Program.AssertOnEventThread();
            Util.ThrowIfEnumerableParameterNullOrEmpty(uris, "uris");
            List<Uri> uriList = new List<Uri>(uris);

            if (uriList.Count == 1)
            {
                Navigate(uriList[0]);
                return;
            }

            // test each url with a WebClient to see if it works.
            _webClients.Clear();
            var proxy = XenAdminConfigManager.Provider.GetProxyFromSettings(null, false);
            _webClients.AddRange(uriList.ConvertAll(u => new WebClient() { Proxy = proxy }));

            // start all urls downloading in parallel.
            for (int i = 0; i < _webClients.Count; i++)
            {
                _webClients[i].DownloadDataCompleted += webClient_DownloadDataCompleted;

                try
                {
                    _webClients[i].DownloadDataAsync(uriList[i], uriList[i]);
                }
                catch (WebException)
                {
                    // we are expecting some urls to fail: do nothing.
                }
                catch (SocketException)
                {
                    // we are expecting some urls to fail: do nothing.
                }
            }
        }

        private void webClient_DownloadDataCompleted(object sender, DownloadDataCompletedEventArgs e)
        {
            Program.AssertOnEventThread();

            WebClient webClient = (WebClient)sender;
            if (_webClients.Contains(webClient))
            {
                _webClients.Remove(webClient);

                if (e.Error == null || (e.Error != null && _webClients.Count == 0))
                {
                    // either one has finished successfully...or...they've all failed.                          
                    // navigate the browser to this url and leave other requests (if any) to timeout.
                    _webClients.Clear();
                    Navigate((Uri)e.UserState);
                }
            }
            else
            {
                // either a valid url has been found...or another Navigate has started: do nothing.
            }
        }

        private class WebBrowserSite2 : WebBrowserSite, Win32.IAuthenticate, Win32.IServiceProvider
        {
            private WebBrowser2 Browser;

            public WebBrowserSite2(WebBrowser2 Browser)
                : base(Browser)
            {
                this.Browser = Browser;
            }

            #region IAuthenticate Members

            public int Authenticate(ref IntPtr phwnd, ref IntPtr pszUsername, ref IntPtr pszPassword)
            {
                string username, password;
                if (Browser.Authenticate(out username, out password))
                {
                    IntPtr sUser = Marshal.StringToCoTaskMemAuto(username);
                    IntPtr sPassword = Marshal.StringToCoTaskMemAuto(password);

                    pszUsername = sUser;
                    pszPassword = sPassword;
                    return Win32.S_OK;
                }
                else
                {
                    return Win32.E_ACCESSDENIED;
                }
            }

            #endregion

            #region IServiceProvider Members

            public int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject)
            {
                if (guidService.CompareTo(Win32.IID_IAuthenticate) == 0 && riid.CompareTo(Win32.IID_IAuthenticate) == 0)
                {
                    ppvObject = Marshal.GetComInterfaceForObject(this, typeof(Win32.IAuthenticate));
                    return Win32.S_OK;
                }
                else
                {
                    ppvObject = IntPtr.Zero;
                    return Win32.INET_E_DEFAULT_ACTION;
                }
            }

            #endregion
        }

        // Handles the NavigateError event from the underlying ActiveX 
        // control by raising the NavigateError event defined in this class.
        private class WebBrowser2EventHelper : 
            StandardOleMarshalObject, DWebBrowserEvents2
        {
            private WebBrowser2 parent;

            public WebBrowser2EventHelper(WebBrowser2 parent)
            {
                this.parent = parent;
            }

            public void NavigateError(object pDisp, ref object url, 
                ref object frame, ref object statusCode, ref bool cancel)
            {
                parent.OnNavigateError(
                    new WebBrowserNavigateErrorEventArgs(
                    (string)url, (string)frame, (Int32)statusCode, cancel));
            }
        }
    }

    public delegate void WebBrowserNavigateErrorEventHandler(object sender, WebBrowserNavigateErrorEventArgs e);

    public class WebBrowserNavigateErrorEventArgs : EventArgs
    {
        public string Url;
        public string Frame;
        public Int32 StatusCode;
        public bool Cancel;

        public WebBrowserNavigateErrorEventArgs(string url, string frame, Int32 statusCode, bool cancel)
        {
            Url = url;
            Frame = frame;
            StatusCode = statusCode;
            Cancel = cancel;
        }
    }

    public delegate void WebBrowserAuthenticationPromptEventHandler(WebBrowser2 sender, WebBrowserAuthenticationPromptEventArgs e);

    public class WebBrowserAuthenticationPromptEventArgs
    {
        /// <summary>
        /// Set to true by the event handler if the user clicked OK on the authentication prompt.
        /// </summary>
        public bool Success;

        /// <summary>
        /// Set by the event handler, iff Success is true.
        /// </summary>
        public string Username;

        /// <summary>
        /// Set by the event handler, iff Success is true.
        /// </summary>
        public string Password;
    }

    [ComImport, Guid("34A715A0-6587-11D0-924A-0020AFC7AC4D"),
    InterfaceType(ComInterfaceType.InterfaceIsIDispatch),
    TypeLibType(TypeLibTypeFlags.FHidden)]
    public interface DWebBrowserEvents2
    {
        [DispId(271)]
        void NavigateError([In, MarshalAs(UnmanagedType.IDispatch)] object pDisp,
                           [In] ref object URL, [In] ref object frame, 
                           [In] ref object statusCode, [In, Out] ref bool cancel);
    }
}
