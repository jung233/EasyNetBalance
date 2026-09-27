use crate::backend::models::{get_bool, get_str};
use serde_json::{json, Value};
use std::collections::HashMap;
use std::net::{IpAddr, SocketAddr, TcpStream};
use std::time::{Duration, Instant};

pub(super) fn get_adapters(settings: &Value) -> Result<Value, String> {
    #[cfg(windows)]
    {
        let overrides = settings.get("interfaceUsabilityOverrides");
        let show_virtual = get_bool(settings, "showVirtualInterfaces", false);
        let mut rows = Vec::new();
        let adapters = ipconfig::get_adapters().map_err(|e| format!("Could not enumerate network adapters: {e}"))?;
        for adapter in adapters {
            let id = adapter.name().trim_matches(['{', '}']).to_owned();
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
            let mut ipv4_gateways = Vec::new(); let mut ipv6_gateways = Vec::new();
            for address in adapter.gateway_addresses() {
                match address { IpAddr::V4(v) => ipv4_gateways.push(v.to_string()), IpAddr::V6(v) => ipv6_gateways.push(v.to_string()) }
            }
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

pub(super) fn probe_adapter(adapter: &Value, family: &str, timeout: Duration) -> Option<f64> {
    let addresses_key = if family.eq_ignore_ascii_case("IPv6") { "ipv6Addresses" } else { "ipv4Addresses" };
    let local = adapter.get(addresses_key)?.as_array()?.iter().filter_map(Value::as_str).filter_map(|s| s.parse::<IpAddr>().ok()).next()?;
    let endpoint = probe_endpoint(family)?;
    let addr = (endpoint, 443);
    let started = Instant::now();
    #[cfg(windows)]
    let result = connect_bound(local, addr, timeout);
    #[cfg(not(windows))]
    let result = TcpStream::connect_timeout(&SocketAddr::new(addr.0, addr.1), timeout).map(|_| ());
    result.ok().map(|_| started.elapsed().as_secs_f64() * 1000.0)
}

fn probe_endpoint(family: &str) -> Option<IpAddr> {
    if family.eq_ignore_ascii_case("IPv6") { "2606:4700:4700::1111".parse().ok() }
    else { "1.1.1.1".parse().ok() }
}

#[cfg(windows)]
fn connect_bound(local: IpAddr, remote: (IpAddr, u16), timeout: Duration) -> std::io::Result<()> {
    if local.is_ipv4() != remote.0.is_ipv4() { return Err(std::io::Error::new(std::io::ErrorKind::InvalidInput, "Address family mismatch")); }
    let domain = if local.is_ipv4() { socket2::Domain::IPV4 } else { socket2::Domain::IPV6 };
    let socket = socket2::Socket::new(domain, socket2::Type::STREAM, Some(socket2::Protocol::TCP))?;
    socket.bind(&SocketAddr::new(local, 0).into())?;
    socket.connect_timeout(&SocketAddr::new(remote.0, remote.1).into(), timeout)?;
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
