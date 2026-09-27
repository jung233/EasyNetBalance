use crate::backend::models::{get_bool, get_str};
use serde_json::{json, Value};
use std::collections::HashMap;
use std::net::{IpAddr, SocketAddr, ToSocketAddrs};
use std::time::{Duration, Instant};

pub(super) fn get_adapters(settings: &Value) -> Result<Value, String> {
    #[cfg(windows)]
    {
        let overrides = settings.get("interfaceUsabilityOverrides");
        let show_virtual = get_bool(settings, "showVirtualInterfaces", false);
        let mut rows = Vec::new();
        let adapters = ipconfig::get_adapters().map_err(|e| format!("Could not enumerate network adapters: {e}"))?;
        for adapter in adapters {
            let id = adapter.adapter_name().trim_matches(['{', '}']).to_owned();
            let name = adapter.friendly_name().to_owned();
            let description = adapter.description().to_owned();
            let kind = format!("{:?}", adapter.if_type());
            let status = format!("{:?}", adapter.oper_status());
            let lower_kind = kind.to_lowercase();
            let virtual_adapter = lower_kind.contains("loopback") || lower_kind.contains("tunnel") || lower_kind.contains("ppp") || lower_kind.contains("software");
            let physical = !virtual_adapter && !lower_kind.contains("unknown");
            if !show_virtual && virtual_adapter { continue; }
            let is_allowed = override_for(overrides, &id).unwrap_or(physical);
            let mut ipv4 = Vec::new(); let mut ipv6 = Vec::new();
            for address in adapter.ip_addresses() {
                match address { IpAddr::V4(v) => ipv4.push(v.to_string()), IpAddr::V6(v) => ipv6.push(v.to_string()) }
            }
            let ipv4_gateways: Vec<String> = Vec::new(); let ipv6_gateways: Vec<String> = Vec::new();
            let dns = adapter.dns_servers().iter().map(ToString::to_string).collect::<Vec<_>>();
            rows.push(json!({
                "id": id, "name": name, "description": description,
                "networkInterfaceType": kind, "operationalStatus": status,
                "speed": 0, "ipv4Addresses": ipv4, "ipv6Addresses": ipv6,
                "ipv4Gateways": ipv4_gateways, "ipv6Gateways": ipv6_gateways,
                "dnsServers": dns, "isPhysical": physical, "isVirtual": virtual_adapter,
                "isUserAllowed": is_allowed, "ipv4Health": "Unknown", "ipv6Health": "Unknown",
                "ipv4Latency": null, "ipv6Latency": null, "lastProbeTime": null
            }));
        }
        rows.sort_by_key(|row| get_str(row, "name").unwrap_or_default().to_lowercase());
        Ok(Value::Array(rows))
    }
    #[cfg(not(windows))]
    {
        let _ = settings;
        Ok(json!([]))
    }
}

fn override_for(overrides: Option<&Value>, id: &str) -> Option<bool> {
    overrides?.as_object()?.iter().find(|(key, _)| key.eq_ignore_ascii_case(id)).and_then(|(_, value)| value.as_bool())
}

pub(super) enum ProbeResult {
    Healthy(f64),
    Down,
    Unavailable,
}

pub(super) fn probe_adapter(adapter: &Value, family: &str, timeout: Duration, endpoints: Option<&Value>) -> ProbeResult {
    let (addresses_key, family_key, ipv6) = if family.eq_ignore_ascii_case("IPv6") {
        ("ipv6Addresses", "requireIPv6", true)
    } else if family.eq_ignore_ascii_case("IPv4") {
        ("ipv4Addresses", "requireIPv4", false)
    } else {
        return ProbeResult::Unavailable;
    };
    let Some(addresses) = adapter.get(addresses_key).and_then(Value::as_array) else { return ProbeResult::Unavailable; };
    let locals = addresses.iter()
        .filter_map(Value::as_str)
        .filter_map(|s| s.parse::<IpAddr>().ok())
        .filter(|address| is_probe_source(*address))
        .collect::<Vec<_>>();
    if locals.is_empty() { return ProbeResult::Unavailable; }
    let remotes = configured_probe_targets(endpoints, family_key, ipv6);
    if remotes.is_empty() { return ProbeResult::Unavailable; }
    let started = Instant::now();
    let attempt_count = (locals.len() * remotes.len()).max(1);
    let mut attempted = 0usize;
    for local in &locals {
        for remote in &remotes {
            let remaining = timeout.saturating_sub(started.elapsed());
            if remaining.is_zero() { return ProbeResult::Down; }
            let attempts_left = (attempt_count - attempted) as u32;
            let attempt_timeout = remaining / attempts_left.max(1);
            attempted += 1;
            if connect_bound(*local, *remote, attempt_timeout).is_ok() {
                return ProbeResult::Healthy(started.elapsed().as_secs_f64() * 1000.0);
            }
        }
    }
    ProbeResult::Down
}

fn configured_probe_targets(endpoints: Option<&Value>, family_key: &str, ipv6: bool) -> Vec<SocketAddr> {
    let mut targets = Vec::new();
    let Some(endpoints) = endpoints.and_then(Value::as_array) else { return targets; };
    for endpoint in endpoints.iter().filter(|item| get_bool(item, "enabled", true) && get_bool(item, family_key, true)) {
        let Ok(url) = get_str(endpoint, "url") else { continue; };
        let Ok(url) = reqwest::Url::parse(&url) else { continue; };
        if url.scheme() != "https" { continue; }
        let Some(host) = url.host_str() else { continue; };
        let Some(port) = url.port_or_known_default() else { continue; };
        let Ok(mut addresses) = (host, port).to_socket_addrs() else { continue; };
        if let Some(address) = addresses.find(|address| address.is_ipv6() == ipv6) {
            if !targets.contains(&address) { targets.push(address); }
        }
    }
    targets
}

fn is_probe_source(address: IpAddr) -> bool {
    match address {
        IpAddr::V4(address) => {
            let octets = address.octets();
            !address.is_unspecified()
                && !address.is_loopback()
                && !address.is_multicast()
                && !(octets[0] == 169 && octets[1] == 254)
        }
        IpAddr::V6(address) => {
            let segments = address.segments();
            !address.is_unspecified()
                && !address.is_loopback()
                && !address.is_multicast()
                // Link-local addresses cannot reach the public probe endpoint.
                && (segments[0] & 0xffc0) != 0xfe80
        }
    }
}

fn connect_bound(local: IpAddr, remote: SocketAddr, timeout: Duration) -> std::io::Result<()> {
    if local.is_ipv4() != remote.is_ipv4() { return Err(std::io::Error::new(std::io::ErrorKind::InvalidInput, "Address family mismatch")); }
    let domain = if local.is_ipv4() { socket2::Domain::IPV4 } else { socket2::Domain::IPV6 };
    let socket = socket2::Socket::new(domain, socket2::Type::STREAM, Some(socket2::Protocol::TCP))?;
    socket.bind(&SocketAddr::new(local, 0).into())?;
    socket.connect_timeout(&remote.into(), timeout)?;
    Ok(())
}

pub(super) fn get_processes() -> Value {
    let mut system = sysinfo::System::new_all();
    system.refresh_all();
    let mut rows: HashMap<String, (String, Vec<u32>)> = HashMap::new();
    for (pid, process) in system.processes() {
        let name = process.name().to_string_lossy().to_string();
        let path = process.exe().map(|p| p.to_string_lossy().to_string());
        let key = path.clone().unwrap_or_else(|| name.clone()).to_lowercase();
        let row = rows.entry(key).or_insert_with(|| (path.unwrap_or_default(), Vec::new()));
        row.1.push(pid.as_u32());
    }
    let mut result: Vec<Value> = rows.into_values().map(|(path, pids)| {
        let process_name = std::path::Path::new(&path).file_name().and_then(|v| v.to_str()).unwrap_or(&path);
        json!({"processName": process_name, "executablePath": if path.is_empty() { Value::Null } else { json!(path) }, "pids": pids})
    }).collect();
    result.sort_by_key(|item| get_str(item, "processName").unwrap_or_default().to_lowercase());
    json!(result)
}
