﻿/*
Technitium Library
Copyright (C) 2020  Shreyas Zare (shreyas@technitium.com)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.

*/

using System;
using System.Diagnostics;
using System.IO;
using TechnitiumLibrary.Net.Proxy;

namespace TechnitiumLibrary.Net.Dns.ClientConnection
{
    public class HttpsClientConnection : DnsClientConnection
    {
        #region constructor

        public HttpsClientConnection(NameServerAddress server, NetProxy proxy)
            : base(DnsTransportProtocol.Https, server, proxy)
        { }

        #endregion

        #region public

        public override DnsDatagram Query(DnsDatagram request, int timeout)
        {
            //serialize request
            byte[] requestBuffer;

            using (MemoryStream mS = new MemoryStream(32))
            {
                request.WriteTo(mS, false);
                requestBuffer = mS.ToArray();
            }

            //DoH wire format request
            Stopwatch stopwatch = new Stopwatch();
            byte[] responseBuffer;

            using (WebClientEx wC = new WebClientEx())
            {
                wC.AddHeader("content-type", "application/dns-message");
                wC.AddHeader("accept", "application/dns-message");
                wC.AddHeader("host", _server.DnsOverHttpEndPoint.Host + ":" + _server.DnsOverHttpEndPoint.Port);
                wC.UserAgent = "DoH client";
                wC.Proxy = _proxy;
                wC.Timeout = timeout;

                if (_proxy == null)
                {
                    if (_server.IPEndPoint == null)
                        _server.RecursiveResolveIPAddress();

                    stopwatch.Start();

                    responseBuffer = wC.UploadData(new Uri(_server.DnsOverHttpEndPoint.Scheme + "://" + _server.IPEndPoint.ToString() + _server.DnsOverHttpEndPoint.PathAndQuery), requestBuffer);

                    stopwatch.Stop();
                }
                else
                {
                    stopwatch.Start();

                    responseBuffer = wC.UploadData(_server.DnsOverHttpEndPoint, requestBuffer);

                    stopwatch.Stop();
                }
            }

            //parse response
            using (MemoryStream mS = new MemoryStream(responseBuffer, false))
            {
                DnsDatagram response = new DnsDatagram(mS, false);

                response.SetMetadata(new DnsDatagramMetadata(_server, _protocol, responseBuffer.Length, stopwatch.Elapsed.TotalMilliseconds));

                if (response.Identifier == request.Identifier)
                    return response;
            }

            return null;
        }

        #endregion
    }
}
