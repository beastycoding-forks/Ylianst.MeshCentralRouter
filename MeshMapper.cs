﻿/*
Copyright 2009-2021 Intel Corporation

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace MeshCentralRouter
{
    public class MeshMapper
    {
        public MeshCentralServer parent = null;
        public int state = 0;
        public string url = null;
        public int protocol = 1; // 1 = TCP, 2 = UDP
        public int localport = 0;
        public int remoteport = 0;
        public string remoteip = null;
        public string certhash = null;
        public int connectCounter = 0;
        public int totalConnectCounter = 0;
        public bool exit = false;
        public bool xdebug = false;
        public bool inaddrany = false;
        TcpListener listener = null;

        // Stats
        public long bytesToServer = 0;
        public long bytesToClient = 0;
        public long bytesToServerCompressed = 0;
        public long bytesToClientCompressed = 0;

        public delegate void onStateMsgChangedHandler(string statemsg);
        public event onStateMsgChangedHandler onStateMsgChanged;

        public static string GetProxyForUrlUsingPac(string DestinationUrl, string PacUri)
        {
            IntPtr WinHttpSession = Win32Api.WinHttpOpen("User", Win32Api.WINHTTP_ACCESS_TYPE_DEFAULT_PROXY, IntPtr.Zero, IntPtr.Zero, 0);

            Win32Api.WINHTTP_AUTOPROXY_OPTIONS ProxyOptions = new Win32Api.WINHTTP_AUTOPROXY_OPTIONS();
            Win32Api.WINHTTP_PROXY_INFO ProxyInfo = new Win32Api.WINHTTP_PROXY_INFO();

            ProxyOptions.dwFlags = Win32Api.WINHTTP_AUTOPROXY_CONFIG_URL;
            ProxyOptions.dwAutoDetectFlags = (Win32Api.WINHTTP_AUTO_DETECT_TYPE_DHCP | Win32Api.WINHTTP_AUTO_DETECT_TYPE_DNS_A);
            ProxyOptions.lpszAutoConfigUrl = PacUri;

            // Get Proxy 
            bool IsSuccess = Win32Api.WinHttpGetProxyForUrl(WinHttpSession, DestinationUrl, ref ProxyOptions, ref ProxyInfo);
            Win32Api.WinHttpCloseHandle(WinHttpSession);

            if (IsSuccess)
            {
                return ProxyInfo.lpszProxy;
            }
            else
            {
                Console.WriteLine("Error: {0}", Win32Api.GetLastError());
                return null;
            }
        }

        // Starts the routing server, called when the start button is pressed
        public void start(MeshCentralServer parent, int protocol, int localPort, string url, int remotePort, string remoteIP)
        {
            this.parent = parent;
            this.protocol = protocol;
            this.remoteport = remotePort;
            this.remoteip = remoteIP;
            this.url = url;
            //wshash = serverHashTextBox.Text;

            Debug(string.Format("MeshMapper-Start: Protcol={0}, LocalPort={1}, Url={2}, RemotePort={3}, RemoteIP={4}", protocol, localPort, url, remotePort, remoteIP));

            if (protocol == 1)
            {
                // Start the TCP listener
                try
                {
                    if (listener != null) return;
                    listener = new TcpListener(inaddrany ? IPAddress.Any : IPAddress.Loopback, localPort);
                    try { listener.Start(); } catch (Exception) { listener = null; }
                    if (listener != null)
                    {
                        listener.BeginAcceptTcpClient(new AsyncCallback(AcceptTcpClientSink), null);
                        localport = ((IPEndPoint)listener.LocalEndpoint).Port;
                        state = 1;
                        UpdateInfo();
                    }
                    else
                    {
                        state = -1;
                        UpdateInfo();
                    }
                }
                catch (Exception ex) {
                    Debug(ex.ToString());
                    stop();
                }
            } else if (protocol == 2) {
                // Start websocket connection
                try
                {
                    IPEndPoint udpEndPoint = new IPEndPoint(inaddrany ? IPAddress.Any : IPAddress.Loopback, localPort);
                    UdpClient client = new UdpClient(udpEndPoint);
                    localport = ((IPEndPoint)client.Client.LocalEndPoint).Port;
                    state = 1;
                    UpdateInfo();
                    ConnectWS(client, ++connectCounter);
                }
                catch (Exception ex) {
                    Debug(ex.ToString());
                    stop();
                }
            }

        }

        public void Debug(string msg) { if (xdebug) { try { File.AppendAllText("debug.log", "Debug-MeshMapper-" + msg + "\r\n"); } catch (Exception) { } } }

        private void UpdateInfo() {
            string msg = "";
            if (state == -1) { msg = Translate.T(Properties.Resources.UnableToBindToLocalPort); }
            else if (state == 0) { msg = Translate.T(Properties.Resources.Stopped2); }
            else if (state == 1) {
                if (remoteip == null)
                {
                    msg = string.Format(Translate.T(Properties.Resources.PortXtoPortY), localport, remoteport);
                } else {
                    msg = string.Format(Translate.T(Properties.Resources.PortXtoIPPortY), localport, remoteip, remoteport);
                }
                if (totalConnectCounter == 1) { msg += Translate.T(Properties.Resources.OneConnection); }
                if (totalConnectCounter > 1) { msg += string.Format(Translate.T(Properties.Resources.ManyConnections), totalConnectCounter); }
            }
            if (onStateMsgChanged != null) { onStateMsgChanged(msg); }
        }

        public void stop()
        {
            Debug("stopButton_Click()");
            exit = true;
            if (listener != null) {
                listener.Stop();
                listener = null;
            }
            localport = 0;
            state = 0;
            UpdateInfo();
        }

        private void ShutdownClients(TcpClient c1, UdpClient c2, webSocketClient c3, int counter)
        {
            Debug("#" + counter + ": ShutdownWebClients()");

            if (c1 != null)
            {
                try
                {
                    if (c1.Client.Connected)
                    {
                        try { c1.Client.Disconnect(false); } catch (Exception) { }
                        try { c1.Client.Close(); } catch (Exception) { }
                        try { c1.Close(); } catch (Exception) { }
                    }
                }
                catch (Exception) { }
            }
            if (c2 != null)
            {
                try { c2.Close(); } catch (Exception) { }
            }
            try { c3.Dispose(); } catch (Exception) { }
        }

        private void AcceptTcpClientSink(IAsyncResult ar)
        {
            TcpClient client = null;
            try
            {
                client = listener.EndAcceptTcpClient(ar);
            }
            catch (Exception) { exit = true; }

            if (client != null)
            {
                // Connect using websocket
                ConnectWS(client, ++connectCounter);
            }

            try
            {
                listener.BeginAcceptTcpClient(new AsyncCallback(AcceptTcpClientSink), null);
            }
            catch (Exception) { exit = true; }
        }

        private void ConnectWS(TcpClient client, int counter)
        {
            webSocketClient wc = new webSocketClient();
            Uri wsurl = new Uri(url + "&auth=" + Uri.EscapeDataString(parent.authCookie));
            Debug("#" + counter + ": Connecting web socket to: " + wsurl.ToString());
            wc.xdebug = xdebug;
            wc.Start(wsurl, certhash);
            wc.tag = client;
            wc.id = counter;
            wc.tunneling = false;
            wc.onStateChanged += Wc_onStateChanged;
            wc.onBinaryData += Wc_onBinaryData;
            wc.onStringData += Wc_onStringData;
        }
        private void ConnectWS(UdpClient client, int counter)
        {
            webSocketClient wc = new webSocketClient();
            Uri wsurl = new Uri(url + "&auth=" + Uri.EscapeDataString(parent.authCookie));
            Debug("#" + counter + ": Connecting web socket to: " + wsurl.ToString());
            wc.xdebug = xdebug;
            wc.Start(wsurl, certhash);
            wc.tag = client;
            wc.id = counter;
            wc.tunneling = false;
            wc.onStateChanged += Wc_onStateChanged;
            wc.onBinaryData += Wc_onBinaryData;
            wc.onStringData += Wc_onStringData;
        }

        private void Wc_onStateChanged(webSocketClient sender, webSocketClient.ConnectionStates state)
        {
            Debug("#" + sender.id + ": Websocket mapping, connected to server.");
            switch (state)
            {
                case webSocketClient.ConnectionStates.Disconnected:
                    {
                        if (sender.tag.GetType() == typeof(TcpClient)) {
                            ShutdownClients((TcpClient)sender.tag, null, sender, sender.id);
                        } else if (sender.tag.GetType() == typeof(UdpClient)) {
                            ShutdownClients(null, (UdpClient)sender.tag, sender, sender.id);
                        }
                        break;
                    }
            }
        }

        private void Wc_onStringData(webSocketClient sender, string data, int orglen)
        {
            bytesToClient += data.Length;
            bytesToClientCompressed += orglen;
            if ((sender.tunneling == false) &&( (data == "c") || (data == "cr")))
            {
                Debug("#" + sender.id + ": Websocket got server 'c' confirmation.");

                // Server confirmed connection, start reading the TCP stream
                //Console.Write("WS-Relay Connect\r\n");

                if (sender.tag == null) return;
                if (sender.tag.GetType() == typeof(TcpClient))
                {
                    TcpClient client = (TcpClient)sender.tag;
                    if (client != null)
                    {
                        byte[] buf1 = new byte[65000];
                        try { client.GetStream().BeginRead(buf1, 0, buf1.Length, new AsyncCallback(ClientEndReadWS), new object[] { this, sender, client, buf1 }); } catch (Exception) { }
                        sender.tunneling = true;
                    }
                }
                if (sender.tag.GetType() == typeof(UdpClient))
                {
                    UdpClient uclient = (UdpClient)sender.tag;
                    try { uclient.BeginReceive(new AsyncCallback(UClientEndReadWS), new object[] { this, sender, uclient }); } catch (Exception) { }
                    sender.tunneling = true;
                }
            }
            else if (sender.tunneling == true)
            {
                Debug("#" + sender.id + ": Websocket got text frame: " + data);
            }
        }

        private void Wc_onBinaryData(webSocketClient sender, byte[] data, int offset, int length, int orglen)
        {
            bytesToClient += length;
            bytesToClientCompressed += orglen;
            Debug("#" + sender.id + ": Websocket binary data: " + length);
            if ((sender.tunneling == false) || (sender.tag == null)) return;

            if (sender.tag.GetType() == typeof(TcpClient))
            {
                // Write: WS --> TCP
                TcpClient client = (TcpClient)sender.tag;
                if (client != null) { try { client.GetStream().Write(data, offset, length); } catch (Exception) { } }
            }
            if ((sender.tag.GetType() == typeof(UdpClient)) && (sender.endpoint != null))
            {
                // Write: WS --> UDP
                UdpClient uclient = (UdpClient)sender.tag;
                if (offset == 0)
                {
                    try { uclient.Send(data, length, sender.endpoint); } catch (Exception) { }
                }
                else
                {
                    byte[] data2 = new byte[length];
                    Array.Copy(data, offset, data2, 0, length);
                    try { uclient.Send(data2, length, sender.endpoint); } catch (Exception) { }
                }
            }
        }


        // Read from the local client
        private void ClientEndReadWS(IAsyncResult ar)
        {
            // Fetch the state
            object[] args = (object[])ar.AsyncState;
            MeshMapper mm = (MeshMapper)args[0];
            webSocketClient wc = (webSocketClient)args[1];
            TcpClient client = (TcpClient)args[2];
            byte[] buf = (byte[])args[3];
            int counter = wc.id;

            int len = 0;
            try
            {
                // Read the data
                if (client != null && client.Connected == true) { len = client.GetStream().EndRead(ar); }
            }
            catch (Exception) { ShutdownClients(client, null, wc, counter); return; }

            //Debug("#" + counter + ": ClientEndRead(" + len + ") - Local Read");
            if (len > 0)
            {
                // Forward the data & read again
                try
                {
                    mm.bytesToServer += len;
                    mm.bytesToServerCompressed += wc.SendBinary(buf, 0, len); // TODO: Do Async
                    try { client.GetStream().BeginRead(buf, 0, buf.Length, new AsyncCallback(ClientEndReadWS), new object[] { mm, wc, client, buf }); } catch (Exception) { }
                }
                catch (Exception)
                {
                    ShutdownClients(client, null, wc, counter);
                    return;
                }
            }
            else
            {
                ShutdownClients(client, null, wc, counter);
                return;
            }
        }

        private void UClientEndReadWS(IAsyncResult ar)
        {
            // Fetch the state
            object[] args = (object[])ar.AsyncState;
            MeshMapper mm = (MeshMapper)args[0];
            webSocketClient wc = (webSocketClient)args[1];
            UdpClient uclient = (UdpClient)args[2];
            int counter = wc.id;

            byte[] buf = null;
            try
            {
                // Read the data
                if (uclient != null) { buf = uclient.EndReceive(ar, ref wc.endpoint); }
            }
            catch (Exception) { ShutdownClients(null, uclient, wc, counter); return; }

            if (buf != null) {
                mm.bytesToServer += buf.Length;
                mm.bytesToServerCompressed += wc.SendBinary(buf, 0, buf.Length); // TODO: Do Async
                try { uclient.BeginReceive(new AsyncCallback(UClientEndReadWS), new object[] { mm, wc, uclient }); } catch (Exception) { }
            }
        }
    }
}
