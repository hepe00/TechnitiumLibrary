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
using System.Collections.Generic;
using System.Net;
using System.Net.Mail;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using TechnitiumLibrary.IO;
using TechnitiumLibrary.Net.Dns.ClientConnection;
using TechnitiumLibrary.Net.Dns.ResourceRecords;
using TechnitiumLibrary.Net.Proxy;

namespace TechnitiumLibrary.Net.Dns
{
    public enum DnsTransportProtocol : byte
    {
        Udp = 0,
        Tcp = 1,
        Tls = 2, //RFC-7858
        Https = 3, //RFC-8484
        HttpsJson = 4 //Google
    }

    public class DnsClient
    {
        #region variables

        static readonly IReadOnlyList<NameServerAddress> ROOT_NAME_SERVERS_IPv4;
        static readonly IReadOnlyList<NameServerAddress> ROOT_NAME_SERVERS_IPv6;

        const int MAX_DELEGATION_HOPS = 16;
        const int MAX_CNAME_HOPS = 16;

        readonly IReadOnlyList<NameServerAddress> _servers;

        NetProxy _proxy;
        bool _preferIPv6 = false;
        int _retries = 2;
        int _timeout = 2000;
        int _threads = 2;

        #endregion

        #region constructor

        static DnsClient()
        {
            ROOT_NAME_SERVERS_IPv4 = new NameServerAddress[]
            {
                new NameServerAddress("a.root-servers.net", IPAddress.Parse("198.41.0.4")), //VeriSign, Inc.
                new NameServerAddress("b.root-servers.net", IPAddress.Parse("192.228.79.201")), //University of Southern California (ISI)
                new NameServerAddress("c.root-servers.net", IPAddress.Parse("192.33.4.12")), //Cogent Communications
                new NameServerAddress("d.root-servers.net", IPAddress.Parse("199.7.91.13")), //University of Maryland
                new NameServerAddress("e.root-servers.net", IPAddress.Parse("192.203.230.10")), //NASA (Ames Research Center)
                new NameServerAddress("f.root-servers.net", IPAddress.Parse("192.5.5.241")), //Internet Systems Consortium, Inc.
                new NameServerAddress("g.root-servers.net", IPAddress.Parse("192.112.36.4")), //US Department of Defense (NIC)
                new NameServerAddress("h.root-servers.net", IPAddress.Parse("198.97.190.53")), //US Army (Research Lab)
                new NameServerAddress("i.root-servers.net", IPAddress.Parse("192.36.148.17")), //Netnod
                new NameServerAddress("j.root-servers.net", IPAddress.Parse("192.58.128.30")), //VeriSign, Inc.
                new NameServerAddress("k.root-servers.net", IPAddress.Parse("193.0.14.129")), //RIPE NCC
                new NameServerAddress("l.root-servers.net", IPAddress.Parse("199.7.83.42")), //ICANN
                new NameServerAddress("m.root-servers.net", IPAddress.Parse("202.12.27.33")) //WIDE Project
            };

            ROOT_NAME_SERVERS_IPv6 = new NameServerAddress[]
            {
                new NameServerAddress("a.root-servers.net", IPAddress.Parse("2001:503:ba3e::2:30")), //VeriSign, Inc.
                new NameServerAddress("b.root-servers.net", IPAddress.Parse("2001:500:84::b")), //University of Southern California (ISI)
                new NameServerAddress("c.root-servers.net", IPAddress.Parse("2001:500:2::c")), //Cogent Communications
                new NameServerAddress("d.root-servers.net", IPAddress.Parse("2001:500:2d::d")), //University of Maryland
                new NameServerAddress("e.root-servers.net", IPAddress.Parse("2001:500:a8::e")), //NASA (Ames Research Center)
                new NameServerAddress("f.root-servers.net", IPAddress.Parse("2001:500:2f::f")), //Internet Systems Consortium, Inc.
                new NameServerAddress("g.root-servers.net", IPAddress.Parse("2001:500:12::d0d")), //US Department of Defense (NIC)
                new NameServerAddress("h.root-servers.net", IPAddress.Parse("2001:500:1::53")), //US Army (Research Lab)
                new NameServerAddress("i.root-servers.net", IPAddress.Parse("2001:7fe::53")), //Netnod
                new NameServerAddress("j.root-servers.net", IPAddress.Parse("2001:503:c27::2:30")), //VeriSign, Inc.
                new NameServerAddress("k.root-servers.net", IPAddress.Parse("2001:7fd::1")), //RIPE NCC
                new NameServerAddress("l.root-servers.net", IPAddress.Parse("2001:500:9f::42")), //ICANN
                new NameServerAddress("m.root-servers.net", IPAddress.Parse("2001:dc3::35")) //WIDE Project
            };
        }

        public DnsClient(Uri dohEndPoint)
        {
            _servers = new NameServerAddress[] { new NameServerAddress(dohEndPoint) };
        }

        public DnsClient(Uri[] dohEndPoints)
        {
            if (dohEndPoints.Length == 0)
                throw new DnsClientException("At least one name server must be available for DnsClient.");

            NameServerAddress[] servers = new NameServerAddress[dohEndPoints.Length];

            for (int i = 0; i < dohEndPoints.Length; i++)
                servers[i] = new NameServerAddress(dohEndPoints[i]);

            _servers = servers;
        }

        public DnsClient(bool preferIPv6 = false)
        {
            _preferIPv6 = preferIPv6;

            IPAddressCollection systemDnsServers = GetSystemDnsServers(_preferIPv6);

            NameServerAddress[] servers = new NameServerAddress[systemDnsServers.Count];

            for (int i = 0; i < systemDnsServers.Count; i++)
                servers[i] = new NameServerAddress(systemDnsServers[i]);

            _servers = servers;
        }

        public DnsClient(IPAddress[] servers)
        {
            if (servers.Length == 0)
                throw new DnsClientException("At least one name server must be available for DnsClient.");

            NameServerAddress[] nameServers = new NameServerAddress[servers.Length];

            for (int i = 0; i < servers.Length; i++)
                nameServers[i] = new NameServerAddress(servers[i]);

            _servers = nameServers;
        }

        public DnsClient(IPAddress server)
            : this(new NameServerAddress(server))
        { }

        public DnsClient(EndPoint server)
            : this(new NameServerAddress(server))
        { }

        public DnsClient(string addresses)
        {
            string[] strServers = addresses.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

            if (strServers.Length == 0)
                throw new DnsClientException("At least one name server must be available for DnsClient.");

            NameServerAddress[] servers = new NameServerAddress[strServers.Length];

            for (int i = 0; i < strServers.Length; i++)
                servers[i] = new NameServerAddress(strServers[i]);

            _servers = servers;
        }

        public DnsClient(NameServerAddress server)
        {
            _servers = new NameServerAddress[] { server };
        }

        public DnsClient(IReadOnlyList<NameServerAddress> servers)
        {
            if (servers.Count == 0)
                throw new DnsClientException("At least one name server must be available for DnsClient.");

            _servers = servers;
        }

        #endregion

        #region static

        public static DnsDatagram RecursiveResolve(DnsQuestionRecord question, IReadOnlyList<NameServerAddress> nameServers = null, IDnsCache cache = null, NetProxy proxy = null, bool preferIPv6 = false, int retries = 2, int timeout = 2000, int maxStackCount = 10)
        {
            if (cache == null)
                cache = new DnsCache();

            IList<NameServerAddress> currentNameServers = null;

            if ((nameServers != null) && (nameServers.Count > 0))
            {
                //create copy of name servers to allow shuffling
                List<NameServerAddress> nameServersCopy = new List<NameServerAddress>(nameServers);

                nameServersCopy.Shuffle();

                if (preferIPv6)
                    nameServersCopy.Sort();

                currentNameServers = nameServersCopy;
            }
            else
            {
                question = new DnsQuestionRecord(question.Name, question.Type, question.Class); //clone question so that original object is not affected
                question.ZoneCut = ""; //enable QNAME minimization by setting zone cut to <root>
            }

            Stack<ResolverData> resolverStack = new Stack<ResolverData>();
            int stackNameServerIndex = 0;
            int hopCount = 0;

            while (true) //stack loop
            {
                if (resolverStack.Count > maxStackCount)
                {
                    while (resolverStack.Count > 0)
                    {
                        ResolverData data = resolverStack.Pop();

                        question = data.Question;
                    }

                    throw new DnsClientException("DnsClient recursive resolution exceeded the maximum stack count for domain: " + question.Name);
                }

                //query cache
                {
                    DnsDatagram request = new DnsDatagram(0, false, DnsOpcode.StandardQuery, false, false, true, false, false, false, DnsResponseCode.NoError, new DnsQuestionRecord[] { question });
                    DnsDatagram cacheResponse = cache.Query(request);

                    switch (cacheResponse.RCODE)
                    {
                        case DnsResponseCode.NoError:
                            if (cacheResponse.Answer.Count > 0)
                            {
                                if (resolverStack.Count == 0)
                                {
                                    return cacheResponse;
                                }
                                else
                                {
                                    ResolverData data = resolverStack.Pop();

                                    question = data.Question;
                                    currentNameServers = data.NameServers;
                                    stackNameServerIndex = data.NameServerIndex;
                                    hopCount = data.HopCount;

                                    switch (cacheResponse.Answer[0].Type)
                                    {
                                        case DnsResourceRecordType.AAAA:
                                            currentNameServers[stackNameServerIndex] = new NameServerAddress(currentNameServers[stackNameServerIndex].Host, new IPEndPoint((cacheResponse.Answer[0].RDATA as DnsAAAARecord).Address, currentNameServers[stackNameServerIndex].Port));
                                            break;

                                        case DnsResourceRecordType.A:
                                            currentNameServers[stackNameServerIndex] = new NameServerAddress(currentNameServers[stackNameServerIndex].Host, new IPEndPoint((cacheResponse.Answer[0].RDATA as DnsARecord).Address, currentNameServers[stackNameServerIndex].Port));
                                            break;

                                        default:
                                            //didnt find IP for current name server
                                            stackNameServerIndex++; //increment to skip current name server
                                            break;
                                    }

                                    //proceed to resolver loop
                                }
                            }
                            else if (cacheResponse.Authority.Count > 0)
                            {
                                if (cacheResponse.Authority[0].Type == DnsResourceRecordType.SOA)
                                {
                                    if (resolverStack.Count == 0)
                                    {
                                        return cacheResponse;
                                    }
                                    else
                                    {
                                        if (question.Type == DnsResourceRecordType.AAAA)
                                        {
                                            question = new DnsQuestionRecord(question.Name, DnsResourceRecordType.A, question.Class);

                                            continue; //to stack loop to query cache for A record
                                        }
                                        else
                                        {
                                            //didnt find IP for current name server
                                            //pop and try next name server
                                            ResolverData data = resolverStack.Pop();

                                            question = data.Question;
                                            currentNameServers = data.NameServers;
                                            stackNameServerIndex = data.NameServerIndex + 1; //increment to skip current name server
                                            hopCount = data.HopCount;

                                            //proceed to resolver loop
                                        }
                                    }
                                }
                                else if ((currentNameServers == null) || (currentNameServers.Count == 0))
                                {
                                    //select only name servers with glue from cache to avoid getting stack overflow due to getting same set of NS records with no address every time from cache
                                    List<NameServerAddress> cacheNameServers = NameServerAddress.GetNameServersFromResponse(cacheResponse, preferIPv6, true);
                                    if (cacheNameServers.Count > 0)
                                    {
                                        cacheNameServers.Shuffle();

                                        if (preferIPv6)
                                            cacheNameServers.Sort();

                                        currentNameServers = cacheNameServers;

                                        if (question.ZoneCut != null)
                                            question.ZoneCut = cacheResponse.Authority[0].Name;
                                    }
                                }
                            }
                            else
                            {
                                if (resolverStack.Count == 0)
                                    return cacheResponse;
                            }

                            break;

                        case DnsResponseCode.NameError:
                            if (resolverStack.Count == 0)
                            {
                                return cacheResponse;
                            }
                            else
                            {
                                //current name server domain doesnt exists
                                //pop and try next name server
                                ResolverData data = resolverStack.Pop();

                                question = data.Question;
                                currentNameServers = data.NameServers;
                                stackNameServerIndex = data.NameServerIndex + 1; //increment to skip current name server
                                hopCount = data.HopCount;

                                //proceed to resolver loop
                                break;
                            }
                    }
                }

                if ((currentNameServers == null) || (currentNameServers.Count == 0))
                {
                    //create copy of root name servers array so that the values in original array are not messed due to shuffling feature
                    if (preferIPv6)
                    {
                        List<NameServerAddress> nameServersCopy = new List<NameServerAddress>(ROOT_NAME_SERVERS_IPv6.Count + ROOT_NAME_SERVERS_IPv4.Count);

                        nameServersCopy.AddRange(ROOT_NAME_SERVERS_IPv6);
                        nameServersCopy.AddRange(ROOT_NAME_SERVERS_IPv4);
                        nameServersCopy.Shuffle();
                        nameServersCopy.Sort();

                        currentNameServers = nameServersCopy;
                    }
                    else
                    {
                        List<NameServerAddress> nameServersCopy = new List<NameServerAddress>(ROOT_NAME_SERVERS_IPv4);
                        nameServersCopy.Shuffle();

                        currentNameServers = nameServersCopy;
                    }
                }

                while (true) //resolver loop
                {
                    //copy and reset stack name server index since its one time use only after stack pop
                    int i = stackNameServerIndex;
                    stackNameServerIndex = 0;

                    Exception lastException = null;

                    //query name servers one by one
                    for (; i < currentNameServers.Count; i++) //try next server loop
                    {
                        NameServerAddress currentNameServer = currentNameServers[i];

                        if ((currentNameServer.IPEndPoint == null) && (proxy == null))
                        {
                            resolverStack.Push(new ResolverData(question, currentNameServers, i, hopCount));

                            if (preferIPv6)
                                question = new DnsQuestionRecord(currentNameServer.Host, DnsResourceRecordType.AAAA, question.Class);
                            else
                                question = new DnsQuestionRecord(currentNameServer.Host, DnsResourceRecordType.A, question.Class);

                            question.ZoneCut = ""; //enable QNAME minimization by setting zone cut to <root>
                            currentNameServers = null;
                            goto stackLoop;
                        }

                        DnsClient client = new DnsClient(currentNameServer);
                        client._proxy = proxy;
                        client._retries = retries;
                        client._timeout = timeout;

                        DnsDatagram request = new DnsDatagram(0, false, DnsOpcode.StandardQuery, false, false, false, false, false, false, DnsResponseCode.NoError, new DnsQuestionRecord[] { question });
                        DnsDatagram response;

                        try
                        {
                            response = client.Resolve(request);
                        }
                        catch (DnsClientException ex)
                        {
                            lastException = ex;
                            continue; //try next name server
                        }

                        cache.CacheResponse(response);

                        switch (response.RCODE)
                        {
                            case DnsResponseCode.NoError:
                                if (response.Answer.Count > 0)
                                {
                                    if (response.Answer[0].Name.Equals(question.Name, StringComparison.OrdinalIgnoreCase))
                                    {
                                        if ((question.Type == DnsResourceRecordType.A) || (question.Type == DnsResourceRecordType.AAAA))
                                        {
                                            //found answer as QNAME minimization uses A or AAAA type queries
                                        }
                                        else if (question.ZoneCut != null)
                                        {
                                            //disable QNAME minimization and query again to current server to get correct type response
                                            question.ZoneCut = null;
                                            i--;
                                            continue;
                                        }
                                    }
                                    else if (question.ZoneCut != null)
                                    {
                                        //disable QNAME minimization and query again to current server
                                        question.ZoneCut = null;
                                        i--;
                                        continue;
                                    }
                                    else
                                    {
                                        //continue to next name server since current name server may be misconfigured
                                        continue;
                                    }

                                    if (resolverStack.Count == 0)
                                    {
                                        return response;
                                    }
                                    else
                                    {
                                        ResolverData data = resolverStack.Pop();

                                        question = data.Question;
                                        currentNameServers = data.NameServers;
                                        stackNameServerIndex = data.NameServerIndex;
                                        hopCount = data.HopCount;

                                        switch (response.Answer[0].Type)
                                        {
                                            case DnsResourceRecordType.AAAA:
                                                currentNameServers[stackNameServerIndex] = new NameServerAddress(currentNameServers[stackNameServerIndex].Host, new IPEndPoint((response.Answer[0].RDATA as DnsAAAARecord).Address, currentNameServers[stackNameServerIndex].Port));
                                                break;

                                            case DnsResourceRecordType.A:
                                                currentNameServers[stackNameServerIndex] = new NameServerAddress(currentNameServers[stackNameServerIndex].Host, new IPEndPoint((response.Answer[0].RDATA as DnsARecord).Address, currentNameServers[stackNameServerIndex].Port));
                                                break;

                                            default:
                                                //didnt find IP for current name server
                                                stackNameServerIndex++; //increment to skip current name server
                                                break;
                                        }

                                        goto resolverLoop;
                                    }
                                }
                                else if (response.Authority.Count > 0)
                                {
                                    if (response.Authority[0].Type == DnsResourceRecordType.SOA)
                                    {
                                        if (question.ZoneCut != null)
                                        {
                                            if (question.Name.Equals(question.MinimizedName, StringComparison.OrdinalIgnoreCase))
                                            {
                                                if ((question.Type == DnsResourceRecordType.A) || (question.Type == DnsResourceRecordType.AAAA))
                                                {
                                                    //record does not exists
                                                }
                                                else
                                                {
                                                    //disable QNAME minimization and query again to current server to get correct type response
                                                    question.ZoneCut = null;
                                                    i--;
                                                    continue;
                                                }
                                            }
                                            else
                                            {
                                                //use minimized name as zone cut and query again to current server to move to next label
                                                question.ZoneCut = question.MinimizedName;
                                                i--;
                                                continue;
                                            }
                                        }

                                        //no entry for given type
                                        if (resolverStack.Count == 0)
                                        {
                                            return response;
                                        }
                                        else
                                        {
                                            if (question.Type == DnsResourceRecordType.AAAA)
                                            {
                                                question = new DnsQuestionRecord(question.Name, DnsResourceRecordType.A, question.Class);

                                                //try same server again with AAAA query
                                                i--;
                                                continue;
                                            }
                                            else
                                            {
                                                //didnt find IP for current name server
                                                //pop and try next name server
                                                ResolverData data = resolverStack.Pop();

                                                question = data.Question;
                                                currentNameServers = data.NameServers;
                                                stackNameServerIndex = data.NameServerIndex + 1; //increment to skip current name server
                                                hopCount = data.HopCount;

                                                goto resolverLoop;
                                            }
                                        }
                                    }
                                    else
                                    {
                                        //check if empty response was received from the authoritative name server
                                        foreach (DnsResourceRecord authorityRecord in response.Authority)
                                        {
                                            if ((authorityRecord.Type == DnsResourceRecordType.NS) && (authorityRecord.RDATA as DnsNSRecord).NameServer.Equals(response.Metadata.NameServerAddress.Host, StringComparison.OrdinalIgnoreCase))
                                            {
                                                //empty response from authoritative name server
                                                if (resolverStack.Count == 0)
                                                {
                                                    return response;
                                                }
                                                else
                                                {
                                                    //unable to resolve current name server domain
                                                    //pop and try next name server
                                                    ResolverData data = resolverStack.Pop();

                                                    question = data.Question;
                                                    currentNameServers = data.NameServers;
                                                    stackNameServerIndex = data.NameServerIndex + 1; //increment to skip current name server
                                                    hopCount = data.HopCount;

                                                    goto resolverLoop;
                                                }
                                            }
                                        }

                                        //check for hop limit
                                        if (hopCount == MAX_DELEGATION_HOPS)
                                        {
                                            //max hop count reached
                                            if (resolverStack.Count == 0)
                                            {
                                                //cannot proceed forever; return what we have and stop
                                                return response;
                                            }
                                            else
                                            {
                                                //unable to resolve current name server domain due to hop limit
                                                //pop and try next name server
                                                ResolverData data = resolverStack.Pop();

                                                question = data.Question;
                                                currentNameServers = data.NameServers;
                                                stackNameServerIndex = data.NameServerIndex + 1; //increment to skip current name server
                                                hopCount = data.HopCount;

                                                goto resolverLoop;
                                            }
                                        }

                                        //get next hop name servers
                                        List<NameServerAddress> nextNameServers = NameServerAddress.GetNameServersFromResponse(response, preferIPv6, false);
                                        if (nextNameServers.Count > 0)
                                        {
                                            nextNameServers.Shuffle();

                                            if (preferIPv6)
                                                nextNameServers.Sort();

                                            if (question.ZoneCut != null)
                                                question.ZoneCut = response.Authority[0].Name;
                                        }

                                        currentNameServers = nextNameServers;

                                        if (currentNameServers.Count == 0)
                                        {
                                            if ((i + 1) == currentNameServers.Count)
                                            {
                                                if (resolverStack.Count == 0)
                                                {
                                                    return response; //return response since this is last name server
                                                }
                                                else
                                                {
                                                    //pop and try next name server
                                                    ResolverData data = resolverStack.Pop();

                                                    question = data.Question;
                                                    currentNameServers = data.NameServers;
                                                    stackNameServerIndex = data.NameServerIndex + 1; //increment to skip current name server
                                                    hopCount = data.HopCount;

                                                    goto resolverLoop;
                                                }
                                            }

                                            continue; //continue to next name server since current name server may be misconfigured
                                        }

                                        goto resolverLoop;
                                    }
                                }
                                else
                                {
                                    if ((i + 1) == currentNameServers.Count)
                                    {
                                        if (resolverStack.Count == 0)
                                        {
                                            return response; //return response since this is last name server
                                        }
                                        else
                                        {
                                            //pop and try next name server
                                            ResolverData data = resolverStack.Pop();

                                            question = data.Question;
                                            currentNameServers = data.NameServers;
                                            stackNameServerIndex = data.NameServerIndex + 1; //increment to skip current name server
                                            hopCount = data.HopCount;

                                            goto resolverLoop;
                                        }
                                    }

                                    continue; //continue to next name server since current name server may be misconfigured
                                }

                            case DnsResponseCode.NameError:
                                if (question.ZoneCut != null)
                                {
                                    if (question.Name.Equals(question.MinimizedName, StringComparison.OrdinalIgnoreCase))
                                    {
                                        //domain does not exists
                                    }
                                    else
                                    {
                                        //disable QNAME minimization and query again to current server to confirm full name response
                                        question.ZoneCut = null;
                                        i--;
                                        continue;
                                    }
                                }

                                if (resolverStack.Count == 0)
                                {
                                    return response;
                                }
                                else
                                {
                                    //current name server domain doesnt exists
                                    //pop and try next name server
                                    ResolverData data = resolverStack.Pop();

                                    question = data.Question;
                                    currentNameServers = data.NameServers;
                                    stackNameServerIndex = data.NameServerIndex + 1; //increment to skip current name server
                                    hopCount = data.HopCount;

                                    goto resolverLoop;
                                }

                            default:
                                if ((i + 1) == currentNameServers.Count)
                                {
                                    if (resolverStack.Count == 0)
                                    {
                                        return response; //return response since this is last name server
                                    }
                                    else
                                    {
                                        //pop and try next name server
                                        ResolverData data = resolverStack.Pop();

                                        question = data.Question;
                                        currentNameServers = data.NameServers;
                                        stackNameServerIndex = data.NameServerIndex + 1; //increment to skip current name server
                                        hopCount = data.HopCount;

                                        goto resolverLoop;
                                    }
                                }

                                continue; //continue to next name server since current name server may be misconfigured
                        }
                    }

                    //no response was received from any of the name servers
                    if (resolverStack.Count == 0)
                    {
                        string strNameServers = null;

                        foreach (NameServerAddress nameServer in currentNameServers)
                        {
                            if (strNameServers == null)
                                strNameServers = nameServer.ToString();
                            else
                                strNameServers += ", " + nameServer.ToString();
                        }

                        throw new DnsClientException("DnsClient recursive resolution failed: no response from name servers [" + strNameServers + "]", lastException);
                    }
                    else
                    {
                        //didnt find IP for current name server
                        //pop and try next name server
                        ResolverData data = resolverStack.Pop();

                        question = data.Question;
                        currentNameServers = data.NameServers;
                        stackNameServerIndex = data.NameServerIndex + 1; //increment to skip current name server
                        hopCount = data.HopCount;

                        //goto resolverLoop;
                    }

                    resolverLoop:;
                }

                stackLoop:;
            }
        }

        public static DnsDatagram RecursiveQuery(DnsQuestionRecord question, IDnsCache cache = null, NetProxy proxy = null, bool preferIPv6 = false, int retries = 2, int timeout = 2000, int maxStackCount = 10)
        {
            if (cache == null)
                cache = new DnsCache();

            DnsDatagram response = RecursiveResolve(question, null, cache, proxy, preferIPv6, retries, timeout, maxStackCount);

            IReadOnlyList<DnsResourceRecord> authority = null;
            IReadOnlyList<DnsResourceRecord> additional = null;

            if (response.Answer.Count > 0)
            {
                DnsResourceRecord lastRR = response.Answer[response.Answer.Count - 1];

                if ((lastRR.Type != question.Type) && (lastRR.Type == DnsResourceRecordType.CNAME) && (question.Type != DnsResourceRecordType.ANY))
                {
                    List<DnsResourceRecord> responseAnswer = new List<DnsResourceRecord>();
                    responseAnswer.AddRange(response.Answer);

                    DnsDatagram lastResponse;
                    int queryCount = 0;

                    do
                    {
                        DnsQuestionRecord cnameQuestion = new DnsQuestionRecord((lastRR.RDATA as DnsCNAMERecord).Domain, question.Type, question.Class);

                        lastResponse = RecursiveResolve(cnameQuestion, null, cache, proxy, preferIPv6, retries, timeout, maxStackCount);

                        if (lastResponse.Answer.Count == 0)
                            break;

                        responseAnswer.AddRange(lastResponse.Answer);

                        lastRR = lastResponse.Answer[lastResponse.Answer.Count - 1];

                        if (lastRR.Type == question.Type)
                            break;

                        if (lastRR.Type != DnsResourceRecordType.CNAME)
                            throw new DnsClientException("Invalid response received from DNS server.");
                    }
                    while (++queryCount < MAX_CNAME_HOPS);

                    if ((lastResponse.Authority.Count > 0) && (lastResponse.Authority[0].Type == DnsResourceRecordType.SOA))
                        authority = lastResponse.Authority;

                    if ((response.Additional.Count > 0) && (question.Type == DnsResourceRecordType.MX))
                        additional = response.Additional;

                    DnsDatagram compositeResponse = new DnsDatagram(0, true, DnsOpcode.StandardQuery, false, false, true, true, false, false, lastResponse.RCODE, new DnsQuestionRecord[] { question }, responseAnswer, authority, additional);

                    if (lastResponse.Metadata != null)
                        compositeResponse.SetMetadata(new DnsDatagramMetadata(lastResponse.Metadata.NameServerAddress, lastResponse.Metadata.Protocol, -1, lastResponse.Metadata.RTT));

                    return compositeResponse;
                }
            }

            if ((response.Authority.Count > 0) && (response.Authority[0].Type == DnsResourceRecordType.SOA))
                authority = response.Authority;

            if ((response.Additional.Count > 0) && (question.Type == DnsResourceRecordType.MX))
                additional = response.Additional;

            DnsDatagram finalResponse = new DnsDatagram(0, true, DnsOpcode.StandardQuery, false, false, true, true, false, false, response.RCODE, new DnsQuestionRecord[] { question }, response.Answer, authority, additional);

            if (response.Metadata != null)
                finalResponse.SetMetadata(new DnsDatagramMetadata(response.Metadata.NameServerAddress, response.Metadata.Protocol, -1, response.Metadata.RTT));

            return finalResponse;
        }

        public static IReadOnlyList<IPAddress> RecursiveResolveIP(string domain, IDnsCache cache = null, NetProxy proxy = null, bool preferIPv6 = false, int retries = 2, int timeout = 2000, int maxStackCount = 10)
        {
            if (cache == null)
                cache = new DnsCache();

            if (preferIPv6)
            {
                IReadOnlyList<IPAddress> addresses = ParseResponseAAAA(RecursiveQuery(new DnsQuestionRecord(domain, DnsResourceRecordType.AAAA, DnsClass.IN), cache, proxy, preferIPv6, retries, timeout, maxStackCount));
                if (addresses.Count > 0)
                    return addresses;
            }

            return ParseResponseA(RecursiveQuery(new DnsQuestionRecord(domain, DnsResourceRecordType.A, DnsClass.IN), cache, proxy, preferIPv6, retries, timeout, maxStackCount));
        }

        public static IReadOnlyList<IPAddress> ParseResponseA(DnsDatagram response)
        {
            string domain = response.Question[0].Name;

            switch (response.RCODE)
            {
                case DnsResponseCode.NoError:
                    if (response.Answer.Count == 0)
                        return Array.Empty<IPAddress>();

                    List<IPAddress> ipAddresses = new List<IPAddress>();

                    foreach (DnsResourceRecord record in response.Answer)
                    {
                        if (record.Name.Equals(domain, StringComparison.OrdinalIgnoreCase))
                        {
                            switch (record.Type)
                            {
                                case DnsResourceRecordType.A:
                                    ipAddresses.Add(((DnsARecord)record.RDATA).Address);
                                    break;

                                case DnsResourceRecordType.CNAME:
                                    domain = ((DnsCNAMERecord)record.RDATA).Domain;
                                    break;
                            }
                        }
                    }

                    return ipAddresses;

                case DnsResponseCode.NameError:
                    throw new NameErrorDnsClientException("Domain does not exists: " + domain + (response.Metadata == null ? "" : "; Name server: " + response.Metadata.NameServerAddress.ToString()));

                default:
                    throw new DnsClientException("Name server returned error. DNS RCODE: " + response.RCODE + " (" + (int)response.RCODE + ")" + (response.Metadata == null ? "" : "; Name server: " + response.Metadata.NameServerAddress.ToString()));
            }
        }

        public static IReadOnlyList<IPAddress> ParseResponseAAAA(DnsDatagram response)
        {
            string domain = response.Question[0].Name;

            switch (response.RCODE)
            {
                case DnsResponseCode.NoError:
                    if (response.Answer.Count == 0)
                        return Array.Empty<IPAddress>();

                    List<IPAddress> ipAddresses = new List<IPAddress>();

                    foreach (DnsResourceRecord record in response.Answer)
                    {
                        if (record.Name.Equals(domain, StringComparison.OrdinalIgnoreCase))
                        {
                            switch (record.Type)
                            {
                                case DnsResourceRecordType.AAAA:
                                    ipAddresses.Add(((DnsAAAARecord)record.RDATA).Address);
                                    break;

                                case DnsResourceRecordType.CNAME:
                                    domain = ((DnsCNAMERecord)record.RDATA).Domain;
                                    break;
                            }
                        }
                    }

                    return ipAddresses;

                case DnsResponseCode.NameError:
                    throw new NameErrorDnsClientException("Domain does not exists: " + domain + (response.Metadata == null ? "" : "; Name server: " + response.Metadata.NameServerAddress.ToString()));

                default:
                    throw new DnsClientException("Name server returned error. DNS RCODE: " + response.RCODE + " (" + (int)response.RCODE + ")" + (response.Metadata == null ? "" : "; Name server: " + response.Metadata.NameServerAddress.ToString()));
            }
        }

        public static IReadOnlyList<string> ParseResponseTXT(DnsDatagram response)
        {
            string domain = response.Question[0].Name;

            switch (response.RCODE)
            {
                case DnsResponseCode.NoError:
                    if (response.Answer.Count == 0)
                        return Array.Empty<string>();

                    List<string> txtRecords = new List<string>();

                    foreach (DnsResourceRecord record in response.Answer)
                    {
                        if (record.Name.Equals(domain, StringComparison.OrdinalIgnoreCase))
                        {
                            switch (record.Type)
                            {
                                case DnsResourceRecordType.TXT:
                                    txtRecords.Add(((DnsTXTRecord)record.RDATA).Text);
                                    break;

                                case DnsResourceRecordType.CNAME:
                                    domain = ((DnsCNAMERecord)record.RDATA).Domain;
                                    break;
                            }
                        }
                    }

                    return txtRecords;

                case DnsResponseCode.NameError:
                    throw new NameErrorDnsClientException("Domain does not exists: " + domain + (response.Metadata == null ? "" : "; Name server: " + response.Metadata.NameServerAddress.ToString()));

                default:
                    throw new DnsClientException("Name server returned error. DNS RCODE: " + response.RCODE + " (" + (int)response.RCODE + ")" + (response.Metadata == null ? "" : "; Name server: " + response.Metadata.NameServerAddress.ToString()));
            }
        }

        public static string ParseResponsePTR(DnsDatagram response)
        {
            string domain = response.Question[0].Name;

            switch (response.RCODE)
            {
                case DnsResponseCode.NoError:
                    if (response.Answer.Count == 0)
                        return null;

                    foreach (DnsResourceRecord record in response.Answer)
                    {
                        if (record.Name.Equals(domain, StringComparison.OrdinalIgnoreCase))
                        {
                            switch (record.Type)
                            {
                                case DnsResourceRecordType.PTR:
                                    return ((DnsPTRRecord)record.RDATA).Domain;

                                case DnsResourceRecordType.CNAME:
                                    domain = ((DnsCNAMERecord)record.RDATA).Domain;
                                    break;
                            }
                        }
                    }

                    return null;

                case DnsResponseCode.NameError:
                    throw new NameErrorDnsClientException("Domain does not exists: " + domain + (response.Metadata == null ? "" : "; Name server: " + response.Metadata.NameServerAddress.ToString()));

                default:
                    throw new DnsClientException("Name server returned error. DNS RCODE: " + response.RCODE + " (" + (int)response.RCODE + ")" + (response.Metadata == null ? "" : "; Name server: " + response.Metadata.NameServerAddress.ToString()));
            }
        }

        public static IReadOnlyList<string> ParseResponseMX(DnsDatagram response)
        {
            string domain = response.Question[0].Name;

            switch (response.RCODE)
            {
                case DnsResponseCode.NoError:
                    if (response.Answer.Count == 0)
                        return Array.Empty<string>();

                    List<DnsMXRecord> mxRecords = new List<DnsMXRecord>();

                    foreach (DnsResourceRecord record in response.Answer)
                    {
                        if (record.Name.Equals(domain, StringComparison.OrdinalIgnoreCase))
                        {
                            switch (record.Type)
                            {
                                case DnsResourceRecordType.MX:
                                    mxRecords.Add((DnsMXRecord)record.RDATA);
                                    break;

                                case DnsResourceRecordType.CNAME:
                                    domain = ((DnsCNAMERecord)record.RDATA).Domain;
                                    break;
                            }
                        }
                    }

                    if (mxRecords.Count > 0)
                    {
                        //sort by mx preference
                        mxRecords.Sort();

                        string[] mxEntries = new string[mxRecords.Count];

                        for (int i = 0; i < mxEntries.Length; i++)
                            mxEntries[i] = mxRecords[i].Exchange;

                        return mxEntries;
                    }

                    return Array.Empty<string>();

                case DnsResponseCode.NameError:
                    throw new NameErrorDnsClientException("Domain does not exists: " + domain + (response.Metadata == null ? "" : "; Name server: " + response.Metadata.NameServerAddress.ToString()));

                default:
                    throw new DnsClientException("Name server returned error. DNS RCODE: " + response.RCODE + " (" + (int)response.RCODE + ")" + (response.Metadata == null ? "" : "; Name server: " + response.Metadata.NameServerAddress.ToString()));
            }
        }

        public static IPAddressCollection GetSystemDnsServers(bool preferIPv6 = false)
        {
            NetworkInfo defaultNetworkInfo;

            if (preferIPv6)
            {
                defaultNetworkInfo = NetUtilities.GetDefaultIPv6NetworkInfo();

                if ((defaultNetworkInfo == null) || (defaultNetworkInfo.Interface.GetIPProperties().DnsAddresses.Count == 0))
                    defaultNetworkInfo = NetUtilities.GetDefaultIPv4NetworkInfo();
            }
            else
            {
                defaultNetworkInfo = NetUtilities.GetDefaultIPv4NetworkInfo();
            }

            if (defaultNetworkInfo == null)
                throw new DnsClientException("No default network connection was found on this computer.");

            IPAddressCollection servers = defaultNetworkInfo.Interface.GetIPProperties().DnsAddresses;

            if (servers.Count == 0)
                throw new DnsClientException("Default network does not have any DNS server configured.");

            return servers;
        }

        public static bool IsDomainNameValid(string domain, bool throwException = false)
        {
            if (domain == null)
            {
                if (throwException)
                    throw new ArgumentNullException(nameof(domain));

                return false;
            }

            if (domain.Length == 0)
                return true; //domain is root zone

            if (domain.Length > 255)
            {
                if (throwException)
                    throw new DnsClientException("Invalid domain name [" + domain + "]: length cannot exceed 255 bytes.");

                return false;
            }

            int labelStart = 0;
            int labelEnd;
            int labelLength;
            int labelChar;
            int i;

            do
            {
                labelEnd = domain.IndexOf('.', labelStart);
                if (labelEnd < 0)
                    labelEnd = domain.Length;

                labelLength = labelEnd - labelStart;

                if (labelLength == 0)
                {
                    if (throwException)
                        throw new DnsClientException("Invalid domain name [" + domain + "]: label length cannot be 0 byte.");

                    return false;
                }

                if (labelLength > 63)
                {
                    if (throwException)
                        throw new DnsClientException("Invalid domain name [" + domain + "]: label length cannot exceed 63 bytes.");

                    return false;
                }

                if (domain[labelStart] == '-')
                {
                    if (throwException)
                        throw new DnsClientException("Invalid domain name [" + domain + "]: label cannot start with hyphen.");

                    return false;
                }

                if (domain[labelEnd - 1] == '-')
                {
                    if (throwException)
                        throw new DnsClientException("Invalid domain name [" + domain + "]: label cannot end with hyphen.");

                    return false;
                }

                if (labelLength != 1 || domain[labelStart] != '*')
                {
                    for (i = labelStart; i < labelEnd; i++)
                    {
                        labelChar = domain[i];

                        if ((labelChar >= 97) && (labelChar <= 122)) //[a-z]
                            continue;

                        if ((labelChar >= 65) && (labelChar <= 90)) //[A-Z]
                            continue;

                        if ((labelChar >= 48) && (labelChar <= 57)) //[0-9]
                            continue;

                        if (labelChar == 45) //[-]
                            continue;

                        if (labelChar == 95) //[_]
                            continue;

                        if (throwException)
                            throw new DnsClientException("Invalid domain name [" + domain + "]: invalid character [" + labelChar + "] was found.");

                        return false;
                    }
                }

                labelStart = labelEnd + 1;
            }
            while (labelEnd < domain.Length);

            return true;
        }

        #endregion

        #region public

        public DnsDatagram Resolve(DnsDatagram request)
        {
            //get servers
            IReadOnlyList<NameServerAddress> servers;
            int threads;

            if (_servers.Count > _threads)
            {
                List<NameServerAddress> serversCopy = new List<NameServerAddress>(_servers);
                serversCopy.Shuffle();

                if (_preferIPv6)
                    serversCopy.Sort();

                servers = serversCopy;
                threads = _threads;
            }
            else
            {
                servers = _servers;
                threads = _servers.Count;
            }

            //init parameters
            object nextServerLock = new object();
            int nextServerIndex = 0;
            IDnsCache dnsCache = null;
            ResolverWaitHandle resolverHandle = new ResolverWaitHandle();

            NameServerAddress GetNextServer()
            {
                lock (nextServerLock)
                {
                    if (nextServerIndex < servers.Count)
                    {
                        NameServerAddress server = servers[nextServerIndex++];

                        if ((dnsCache == null) && server.IsIPEndPointStale && (_proxy == null))
                            dnsCache = new DnsCache();

                        return server;
                    }

                    return null; //no next server available; stop thread
                }
            }

            void ResolveAsync(object state)
            {
                try
                {
                    DnsDatagram asyncRequest = request.Clone();

                    while (true) //next server loop
                    {
                        if (resolverHandle.WaitHandle.WaitOne(0))
                            return; //already signalled

                        NameServerAddress server = GetNextServer();
                        if (server == null)
                            return; //all servers done

                        if (server.IsIPEndPointStale && (_proxy == null))
                        {
                            //recursive resolve name server via root servers when proxy is null else let proxy resolve it
                            try
                            {
                                server.RecursiveResolveIPAddress(dnsCache, null, _preferIPv6, _retries, _timeout);
                            }
                            catch (Exception ex)
                            {
                                resolverHandle.LastException = ex;
                                continue; //failed to resolve name server; try next server
                            }
                        }

                        DnsTransportProtocol protocol = server.Protocol;

                        //upgrade protocol to TCP when UDP is not supported by proxy and server is not bypassed
                        if ((_proxy != null) && (protocol == DnsTransportProtocol.Udp) && !_proxy.IsBypassed(server.EndPoint) && !_proxy.IsUdpAvailable())
                            protocol = DnsTransportProtocol.Tcp;

                        asyncRequest.SetRandomIdentifier();

                        bool switchProtocol;
                        do //switch protocol loop
                        {
                            switchProtocol = false;

                            //get connection
                            DnsClientConnection connection;

                            if ((request.Question.Count > 0) && (request.Question[0].Type == DnsResourceRecordType.AXFR))
                            {
                                //use separate connection for zone transfer
                                if (protocol == DnsTransportProtocol.Udp)
                                    protocol = DnsTransportProtocol.Tcp;

                                switch (protocol)
                                {
                                    case DnsTransportProtocol.Tcp:
                                        connection = new TcpClientConnection(server, _proxy);
                                        break;

                                    case DnsTransportProtocol.Tls:
                                        connection = new TlsClientConnection(server, _proxy);
                                        break;

                                    default:
                                        throw new NotSupportedException("DNS transport protocol not supported for zone transfer.");
                                }
                            }
                            else
                            {
                                //use pooled connection
                                connection = DnsClientConnection.GetConnection(protocol, server, _proxy);
                            }

                            using (connection)
                            {
                                //query server
                                int retry = 0;
                                while (retry < _retries) //retry loop
                                {
                                    retry++;

                                    if (resolverHandle.WaitHandle.WaitOne(0))
                                        return; //already signalled

                                    try
                                    {
                                        DnsDatagram response = connection.Query(asyncRequest, _timeout);
                                        if (response != null)
                                        {
                                            //got response
                                            if (response.Truncation)
                                            {
                                                if (protocol == DnsTransportProtocol.Udp)
                                                {
                                                    protocol = DnsTransportProtocol.Tcp;
                                                    switchProtocol = true;
                                                    break;
                                                }

                                                //ignore TC response
                                            }
                                            else
                                            {
                                                //signal waiting thread
                                                resolverHandle.Response = response;
                                                resolverHandle.WaitHandle.Set();
                                                return;
                                            }
                                        }
                                    }
                                    catch (SocketException ex)
                                    {
                                        resolverHandle.LastException = ex;

                                        switch (ex.SocketErrorCode)
                                        {
                                            case SocketError.ConnectionReset:
                                            case SocketError.HostUnreachable:
                                            case SocketError.MessageSize:
                                            case SocketError.NetworkReset:
                                            case SocketError.NetworkUnreachable:
                                            case SocketError.ConnectionRefused:
                                            case SocketError.TimedOut:
                                                retry = _retries; //dont retry
                                                break;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        resolverHandle.LastException = ex;
                                    }
                                }
                            }
                        }
                        while (switchProtocol); //switch protocol loop
                    }
                }
                catch (Exception ex)
                {
                    resolverHandle.LastException = ex;
                }
            }

            if (threads > 1)
            {
                //start worker threads
                for (int i = 0; i < threads; i++)
                    ThreadPool.QueueUserWorkItem(ResolveAsync);

                //wait for first response
                resolverHandle.WaitHandle.WaitOne(_timeout * _retries);
            }
            else
            {
                //resolve in current thread
                ResolveAsync(null);
            }

            if (resolverHandle.Response == null)
                throw new DnsClientException("DnsClient failed to resolve the request: no response from name servers.", resolverHandle.LastException);

            return resolverHandle.Response;
        }

        public DnsDatagram Resolve(DnsQuestionRecord question)
        {
            return Resolve(new DnsDatagram(0, false, DnsOpcode.StandardQuery, false, false, true, false, false, false, DnsResponseCode.NoError, new DnsQuestionRecord[] { question }));
        }

        public DnsDatagram Resolve(string domain, DnsResourceRecordType type)
        {
            if ((type == DnsResourceRecordType.PTR) && IPAddress.TryParse(domain, out IPAddress address))
                return Resolve(new DnsQuestionRecord(address, DnsClass.IN));
            else
                return Resolve(new DnsQuestionRecord(domain, type, DnsClass.IN));
        }

        public IReadOnlyList<string> ResolveMX(MailAddress emailAddress, bool resolveIP = false, bool preferIPv6 = false)
        {
            return ResolveMX(emailAddress.Host, resolveIP, preferIPv6);
        }

        public IReadOnlyList<string> ResolveMX(string domain, bool resolveIP = false, bool preferIPv6 = false)
        {
            if (IPAddress.TryParse(domain, out _))
            {
                //host is valid ip address
                return new string[] { domain };
            }

            DnsDatagram response = Resolve(new DnsQuestionRecord(domain, DnsResourceRecordType.MX, DnsClass.IN));
            IReadOnlyList<string> mxEntries = ParseResponseMX(response);

            if (!resolveIP)
                return mxEntries;

            //resolve IP addresses
            List<string> mxAddresses = new List<string>();

            //check glue records
            foreach (string mxEntry in mxEntries)
            {
                bool glueRecordFound = false;

                foreach (DnsResourceRecord record in response.Additional)
                {
                    if (record.Name.Equals(mxEntry, StringComparison.OrdinalIgnoreCase))
                    {
                        switch (record.Type)
                        {
                            case DnsResourceRecordType.A:
                                if (!preferIPv6)
                                {
                                    mxAddresses.Add(((DnsARecord)record.RDATA).Address.ToString());
                                    glueRecordFound = true;
                                }
                                break;

                            case DnsResourceRecordType.AAAA:
                                if (preferIPv6)
                                {
                                    mxAddresses.Add(((DnsAAAARecord)record.RDATA).Address.ToString());
                                    glueRecordFound = true;
                                }
                                break;
                        }
                    }
                }

                if (!glueRecordFound)
                {
                    try
                    {
                        IReadOnlyList<IPAddress> ipList = ResolveIP(mxEntry, preferIPv6);

                        foreach (IPAddress ip in ipList)
                            mxAddresses.Add(ip.ToString());
                    }
                    catch (NameErrorDnsClientException)
                    { }
                    catch (DnsClientException)
                    {
                        mxAddresses.Add(mxEntry);
                    }
                }
            }

            return mxAddresses;
        }

        public string ResolvePTR(IPAddress ip)
        {
            return ParseResponsePTR(Resolve(new DnsQuestionRecord(ip, DnsClass.IN)));
        }

        public IReadOnlyList<string> ResolveTXT(string domain)
        {
            return ParseResponseTXT(Resolve(new DnsQuestionRecord(domain, DnsResourceRecordType.TXT, DnsClass.IN)));
        }

        public IReadOnlyList<IPAddress> ResolveIP(string domain, bool preferIPv6 = false)
        {
            if (preferIPv6)
            {
                IReadOnlyList<IPAddress> addresses = ParseResponseAAAA(Resolve(new DnsQuestionRecord(domain, DnsResourceRecordType.AAAA, DnsClass.IN)));
                if (addresses.Count > 0)
                    return addresses;
            }

            return ParseResponseA(Resolve(new DnsQuestionRecord(domain, DnsResourceRecordType.A, DnsClass.IN)));
        }

        #endregion

        #region property

        public IReadOnlyList<NameServerAddress> Servers
        { get { return _servers; } }

        public NetProxy Proxy
        {
            get { return _proxy; }
            set { _proxy = value; }
        }

        public bool PreferIPv6
        {
            get { return _preferIPv6; }
            set { _preferIPv6 = value; }
        }

        public int Retries
        {
            get { return _retries; }
            set { _retries = value; }
        }

        public int Timeout
        {
            get { return _timeout; }
            set { _timeout = value; }
        }

        public int Threads
        {
            get { return _threads; }
            set { _threads = value; }
        }

        #endregion

        class ResolverData
        {
            public readonly DnsQuestionRecord Question;
            public readonly IList<NameServerAddress> NameServers;
            public readonly int NameServerIndex;
            public readonly int HopCount;

            public ResolverData(DnsQuestionRecord question, IList<NameServerAddress> nameServers, int nameServerIndex, int hopCount)
            {
                Question = question;
                NameServers = nameServers;
                NameServerIndex = nameServerIndex;
                HopCount = hopCount;
            }
        }

        class ResolverWaitHandle
        {
            public readonly EventWaitHandle WaitHandle = new ManualResetEvent(false);
            public volatile DnsDatagram Response;
            public volatile Exception LastException;
        }
    }
}
