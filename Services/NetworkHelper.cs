// Copyright (c) 2026 Holger Imbery. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PigeonPost.Services;

/// <summary>
/// Utility methods for discovering the local machine IP addresses that
/// the HTTP listener should bind to.
/// </summary>
public static class NetworkHelper
{
    /// <summary>
    /// Returns the outbound IPv4 address the OS would use to reach the internet.
    /// This is a best-effort "primary" IP used purely for display in the dashboard;
    /// it is not guaranteed to be reachable from other devices on all network topologies.
    /// </summary>
    public static string GetPrimaryLocalIp()
    {
        try
        {
            // Connect a UDP socket (no packets actually sent) to force the OS to
            // select the default outbound interface, then read its local address.
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            s.Connect("8.8.8.8", 80);
            var ep = (IPEndPoint?)s.LocalEndPoint;
            return ep?.Address.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    /// <summary>
    /// Enumerates every IPv4 address bound to a non-loopback, operational network interface.
    ///
    /// <para>
    /// <see cref="System.Net.HttpListener"/> on Windows requires a URL ACL entry for wildcard
    /// prefixes (<c>http://+:.../</c>), which normally needs administrator privileges.
    /// By registering one explicit prefix per interface address we avoid the wildcard ACL
    /// requirement and the app can run without elevation.
    /// </para>
    /// </summary>
    public static IEnumerable<string> GetAllBindableIPv4()
    {
        var seen = new HashSet<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            // Skip interfaces that are down or are the loopback adapter.
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                // Only IPv4; skip link-local / IPv6.
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                // Deduplicate in the unlikely event two NICs share an address.
                if (seen.Add(ua.Address.ToString()))
                    yield return ua.Address.ToString();
            }
        }
    }
}