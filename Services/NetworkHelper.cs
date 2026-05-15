// Copyright (c) 2026 Holger Imbery. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PigeonPost.Services;

/// <summary>Describes which physical medium provides the active network connection.</summary>
public enum NetworkInterfaceKind
{
    /// <summary>No usable network interface is up (offline).</summary>
    None,

    /// <summary>The primary interface is a wireless (802.11 Wi-Fi) adapter.</summary>
    WiFi,

    /// <summary>The primary interface is a wired Ethernet (or virtual) adapter.</summary>
    Ethernet,
}

/// <summary>
/// Utility methods for discovering the local machine IP addresses that
/// the HTTP listener should bind to.
/// </summary>
public static class NetworkHelper
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="nic"/> is a known hypervisor or
    /// software virtual switch adapter that should be excluded from the listen-address
    /// selection and listener binding.
    ///
    /// <para>Excluded adapter families:</para>
    /// <list type="bullet">
    ///   <item><term>Hyper-V</term>     <description>vEthernet adapters created by the Hyper-V Virtual Switch (name starts with "vEthernet" or description contains "Hyper-V Virtual Ethernet").</description></item>
    ///   <item><term>VMware</term>      <description>Host-only / NAT adapters created by VMware Workstation / Player (description contains "VMware Virtual Ethernet").</description></item>
    ///   <item><term>VirtualBox</term>  <description>Host-only adapters created by Oracle VirtualBox (description contains "VirtualBox Host-Only").</description></item>
    /// </list>
    ///
    /// <para>
    /// Real physical Ethernet adapters, Wi-Fi adapters, and Tailscale are not affected.
    /// </para>
    /// </summary>
    private static bool IsVirtualAdapter(NetworkInterface nic)
    {
        // Honour the user's preference — if exclusion is disabled, treat every adapter as real.
        if (!SettingsService.Current.ExcludeVirtualAdapters) return false;

        var name = nic.Name        ?? string.Empty;
        var desc = nic.Description ?? string.Empty;

        // Hyper-V virtual switch ports — name always starts with "vEthernet"
        if (name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase)) return true;

        // Belt-and-suspenders: match the description as well
        if (desc.Contains("Hyper-V Virtual Ethernet",  StringComparison.OrdinalIgnoreCase)) return true;
        if (desc.Contains("VMware Virtual Ethernet",   StringComparison.OrdinalIgnoreCase)) return true;
        if (desc.Contains("VirtualBox Host-Only",      StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary>
    /// Returns the current primary IPv4 address together with the kind of interface
    /// it belongs to (Wi-Fi, Ethernet, or None when offline).
    ///
    /// <para>
    /// Wi-Fi adapters are preferred when both types are simultaneously active so the
    /// result is deterministic in mixed environments.
    /// Virtual/hypervisor adapters (Hyper-V, VMware, VirtualBox) are excluded.
    /// </para>
    /// </summary>
    public static (string Ip, NetworkInterfaceKind Kind) GetNetworkState()
    {
        string? wifiIp = null;
        string? etherIp = null;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
            if (IsVirtualAdapter(nic)) continue;   // skip Hyper-V / VMware / VirtualBox vSwitches

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                var ip = ua.Address.ToString();
                if (IsTailscaleIp(ip)) continue;   // exclude Tailscale overlay IPs
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                    wifiIp ??= ip;     // first Wi-Fi address wins
                else
                    etherIp ??= ip;   // first Ethernet/other address wins
            }
        }

        // Prefer Wi-Fi when both are present; return None + loopback when offline.
        if (wifiIp  != null) return (wifiIp,  NetworkInterfaceKind.WiFi);
        if (etherIp != null) return (etherIp, NetworkInterfaceKind.Ethernet);
        return ("127.0.0.1", NetworkInterfaceKind.None);
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
            if (IsVirtualAdapter(nic)) continue;   // skip Hyper-V / VMware / VirtualBox vSwitches

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

    /// <summary>
    /// Returns <c>true</c> if <paramref name="ip"/> falls within the Tailscale CGNAT range
    /// <c>100.64.0.0/10</c> (100.64.x.x – 100.127.x.x).
    /// </summary>
    public static bool IsTailscaleIp(string ip)
    {
        if (!IPAddress.TryParse(ip, out var addr)) return false;
        var b = addr.GetAddressBytes();
        return b.Length == 4 && b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }

    /// <summary>
    /// Scans all operational network interfaces (including virtual/tunnel adapters)
    /// and returns the first IPv4 address in the Tailscale CGNAT range
    /// (<c>100.64.0.0/10</c>), or <c>null</c> when Tailscale is not connected.
    /// </summary>
    public static string? GetTailscaleIp()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var ip = ua.Address.ToString();
                if (IsTailscaleIp(ip)) return ip;
            }
        }
        return null;
    }
}