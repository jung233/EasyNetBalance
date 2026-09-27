mod mihomo;
mod models;
mod network;
mod pipe;

use base64::Engine;
use chrono::{DateTime, Utc};
use models::{default_settings, get_bool, get_i64, get_str, merge_defaults};
use serde_json::{json, Value};
use std::collections::{HashMap, VecDeque};
use std::fs;
use std::io::{Cursor, Write};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

pub(crate) use pipe::run_service;

const PIPE_NAME: &str = "EasyBalance.Control.v1";
const PRODUCT_NAME: &str = "EasyNetBalance";

#[derive(Clone)]
struct LogEntry {
    timestamp: DateTime<Utc>,
    level: String,
    source: String,
    message: String,
}

pub(super) struct Runtime {
    data_dir: PathBuf,
    settings: Value,
    core: mihomo::MihomoCore,
    logs: Arc<Mutex<VecDeque<LogEntry>>>,
    adapter_health: HashMap<(String, String), HealthRecord>,
    traffic_seen: HashMap<String, (String, u64, u64)>,
    traffic_totals: HashMap<String, (u64, u64)>,
    active_routes: HashMap<(String, String), String>,
    failovers: u64,
    failbacks: u64,
    last_error: Option<String>,
    started_at: Option<Instant>,
    probe_count: u64,
    successful_probes: u64,
    failed_probes: u64,
}

#[derive(Clone, Default)]
struct HealthRecord {
    state: String,
    latency_ms: Option<f64>,
    last_probe: Option<DateTime<Utc>>,
    recovered_since: Option<DateTime<Utc>>,
    failures: u32,
    successes: u32,
}

impl Runtime {
    fn new() -> Result<Self, String> {
        let data_dir = data_directory();
        fs::create_dir_all(&data_dir).map_err(|e| format!("Could not create the service data directory: {e}"))?;
        secure_data_directory(&data_dir)?;
        let settings_path = data_dir.join("settings.json");
        let legacy_settings = std::env::var_os("PROGRAMDATA").map(PathBuf::from).unwrap_or_else(|| PathBuf::from(r"C:\ProgramData"))
            .join("EasyBalance").join("settings.json");
        if !settings_path.exists() && legacy_settings.is_file() {
            fs::copy(&legacy_settings, &settings_path).map_err(|e| format!("Could not migrate existing settings: {e}"))?;
        }
        let mut settings = if settings_path.exists() {
            let bytes = fs::read(&settings_path).map_err(|e| format!("Could not read settings: {e}"))?;
            serde_json::from_slice(&bytes).map_err(|e| format!("Settings file is invalid: {e}"))?
        } else {
            default_settings()
        };
        merge_defaults(&mut settings, default_settings());
        let logs = Arc::new(Mutex::new(VecDeque::with_capacity(500)));
        let mut runtime = Self {
            data_dir: data_dir.clone(),
            settings,
            core: mihomo::MihomoCore::new(data_dir, logs.clone()),
            logs,
            adapter_health: HashMap::new(),
            traffic_seen: HashMap::new(),
            traffic_totals: HashMap::new(),
            active_routes: HashMap::new(),
            failovers: 0,
            failbacks: 0,
            last_error: None,
            started_at: None,
            probe_count: 0,
            successful_probes: 0,
            failed_probes: 0,
        };
        if get_bool(&runtime.settings, "enabled", false) {
            let adapters = network::get_adapters(&runtime.settings)?;
            runtime.start_core(&adapters)?;
        }
        runtime.log("Information", "Service", "EasyNetBalance service initialized.");
        Ok(runtime)
    }

    pub(super) fn handle(&mut self, method: &str, payload: Value) -> Result<Value, String> {
        match method {
            "GetStatus" => Ok(self.status()),
            "GetCapabilities" => Ok(json!({"supportsTrafficRatio": self.core.is_running() && self.core.supports_weighted_bytes(), "supportsProcessRules": true, "supportsTun": true, "core": "mihomo"})),
            "GetAdapters" => self.get_adapters(),
            "GetRules" => Ok(array_at(&self.settings, "applicationRules")),
            "GetPolicies" => Ok(self.policies()),
            "GetSettings" => Ok(self.settings.clone()),
            "GetLogs" => Ok(self.get_logs()),
            "GetDiagnostics" => Ok(self.diagnostics()),
            "GetConnectionTelemetry" => self.connection_telemetry(),
            "GetProcesses" => Ok(network::get_processes()),
            "GetGeneratedConfig" => Ok(json!({"json": self.core.redacted_config()?})),
            "SaveRule" => self.save_rule(payload),
            "DeleteRule" => self.delete_item("applicationRules", &get_str(&payload, "id")?),
            "SavePolicy" => self.save_policy(payload),
            "DeletePolicy" => self.delete_policy(&get_str(&payload, "id")?),
            "SetDefaultPolicy" => self.set_default_policy(&get_str(&payload, "id")?),
            "SetTrafficRatio" => self.set_traffic_ratio(get_i64(&payload, "primaryTrafficPercent")?),
            "SaveSettings" => self.save_settings(payload),
            "SetInterfaceUsability" => self.set_interface_usability(payload),
            "EnableRouting" => self.set_routing(true),
            "DisableRouting" => self.set_routing(false),
            "RestartCore" => self.restart_core(),
            "ValidateConfig" => self.validate_config(),
            "TestInterface" => self.test_interface(payload),
            "ExportDiagnostics" => self.export_diagnostics(payload),
            _ => Err(format!("Unknown method: {method}")),
        }
    }

    fn status(&self) -> Value {
        let active = self.core.is_running();
        let policy_status: Vec<Value> = self.policies().as_array().into_iter().flatten().map(|p| {
            let id = get_str(p, "id").unwrap_or_default();
            let name = get_str(p, "name").unwrap_or_else(|_| "Policy".to_owned());
            let v4 = self.active_target(&id, "IPv4");
            let v6 = self.active_target(&id, "IPv6");
            json!({
                "policyId": id, "name": name,
                "activeIPv4Interface": v4, "activeIPv6Interface": v6,
                "noHealthyIPv4Interface": v4.is_none(), "noHealthyIPv6Interface": v6.is_none()
            })
        }).collect();
        json!({
            "routingEnabled": get_bool(&self.settings, "enabled", false),
            "coreRunning": active,
            "coreFaulted": self.last_error.is_some() && !active,
            "version": self.core.version(),
            "uptime": self.started_at.map(|t| format_timespan(t.elapsed())),
            "lastError": self.last_error,
            "lastExitCode": self.core.last_exit_code(),
            "weightedBytesSupported": active && self.core.supports_weighted_bytes(),
            "policies": policy_status
        })
    }

    fn policies(&self) -> Value {
        let mut values = vec![self.settings.get("defaultPolicy").cloned().unwrap_or(Value::Null)];
        if let Some(items) = self.settings.get("policies").and_then(Value::as_array) {
            let default_id = get_str(&values[0], "id").unwrap_or_default();
            values.extend(items.iter().filter(|item| get_str(item, "id").unwrap_or_default() != default_id).cloned());
        }
        Value::Array(values)
    }

    fn get_adapters(&mut self) -> Result<Value, String> {
        let mut items = network::get_adapters(&self.settings)?;
        for item in items.as_array_mut().into_iter().flatten() {
            let id = get_str(item, "id").unwrap_or_default();
            for family in ["IPv4", "IPv6"] {
                let state = self.adapter_health.get(&(id.clone(), family.to_owned())).cloned().unwrap_or_default();
                let prefix = if family == "IPv4" { "ipv4" } else { "ipv6" };
                item[format!("{prefix}Health")] = json!(if state.state.is_empty() { "Unknown" } else { &state.state });
                if let Some(ms) = state.latency_ms { item[format!("{prefix}Latency")] = json!(format_timespan(Duration::from_secs_f64(ms / 1000.0))); }
                if let Some(time) = state.last_probe { item["lastProbeTime"] = json!(time.to_rfc3339()); }
            }
        }
        Ok(items)
    }

    pub(super) fn tick(&mut self) -> Result<(), String> {
        if !get_bool(&self.settings, "enabled", false) || !self.core.is_running() { return Ok(()); }
        let now = Utc::now();
        let adapters = network::get_adapters(&self.settings)?;
        for adapter in adapters.as_array().into_iter().flatten().filter(|a| get_bool(a, "isUserAllowed", false)) {
            let id = get_str(adapter, "id").unwrap_or_default();
            for family in ["IPv4", "IPv6"] {
                if family == "IPv6" && !get_bool(&self.settings, "ipv6Enabled", true) { continue; }
                let key = (id.clone(), family.to_owned());
                let current = self.adapter_health.get(&key).cloned().unwrap_or_default();
                let interval = if current.state == "Down" { setting_seconds(&self.settings, "downProbeInterval", 8) } else { setting_seconds(&self.settings, "healthyProbeInterval", 20) };
                if current.last_probe.is_some_and(|last| (now - last).num_seconds() < interval as i64) { continue; }
                let latency = network::probe_adapter(adapter, family, Duration::from_secs(setting_seconds(&self.settings, "probeTimeout", 3)));
                self.probe_count += 1;
                if latency.is_some() { self.successful_probes += 1; } else { self.failed_probes += 1; }
                let record = self.adapter_health.entry(key).or_default();
                record.last_probe = Some(now);
                record.latency_ms = latency;
                let failure_threshold = get_i64(&self.settings["defaultPolicy"], "failureThreshold").unwrap_or(3).max(1) as u32;
                let recovery_threshold = get_i64(&self.settings["defaultPolicy"], "recoveryThreshold").unwrap_or(3).max(1) as u32;
                if latency.is_some() {
                    record.failures = 0;
                    record.successes += 1;
                    if record.successes >= recovery_threshold {
                        if record.state != "Healthy" { record.recovered_since = Some(now); }
                        record.state = "Healthy".to_owned();
                    } else if record.state != "Healthy" { record.state = "Recovering".to_owned(); }
                } else {
                    record.successes = 0;
                    record.failures += 1;
                    if record.failures >= failure_threshold { record.state = "Down".to_owned(); record.recovered_since = None; }
                    else if record.state != "Down" { record.state = "Suspect".to_owned(); }
                }
            }
        }
        self.evaluate_failover(now);
        Ok(())
    }

    fn evaluate_failover(&mut self, now: DateTime<Utc>) {
        let policies = self.policies().as_array().cloned().unwrap_or_default();
        for policy in policies.iter().filter(|p| get_bool(p, "enabled", true)) {
            let policy_id = get_str(policy, "id").unwrap_or_default();
            let primary = get_str(policy, "primaryInterfaceId").unwrap_or_default();
            if primary.is_empty() { continue; }
            for family in ["IPv4", "IPv6"] {
                if family == "IPv6" && !get_bool(&self.settings, "ipv6Enabled", true) { continue; }
                let current = self.active_routes.get(&(policy_id.clone(), family.into())).cloned().unwrap_or_else(|| primary.clone());
                let primary_state = self.adapter_health.get(&(primary.clone(), family.into())).map(|v| v.state.as_str()).unwrap_or("Unknown");
                let desired = if matches!(primary_state, "Down" | "Unavailable") && get_bool(policy, "failoverEnabled", true) {
                    get_str(policy, "fallbackInterfaceId").ok().filter(|fallback| {
                        self.adapter_health.get(&(fallback.clone(), family.into())).is_some_and(|record| record.state == "Healthy")
                    }).unwrap_or_else(|| current.clone())
                } else if current != primary && get_bool(policy, "autoFailback", true) && primary_state == "Healthy" {
                    let hold = setting_seconds(policy, "minimumSwitchHoldTime", 15);
                    let recovered = self.adapter_health.get(&(primary.clone(), family.into())).and_then(|v| v.recovered_since).unwrap_or(now);
                    if (now - recovered).num_seconds() >= hold as i64 { primary.clone() } else { current.clone() }
                } else { current.clone() };
                if desired == current { continue; }
                let outbound = mihomo::outbound_tag(&desired);
                match self.core.switch_selector(&policy_id, family, &outbound) {
                    Ok(()) => {
                        self.active_routes.insert((policy_id.clone(), family.into()), desired.clone());
                        if desired == primary { self.failbacks += 1; } else { self.failovers += 1; }
                        self.log("Information", "Failover", &format!("{} {family}: {desired}", get_str(policy, "name").unwrap_or_default()));
                    }
                    Err(error) => { self.last_error = Some(error.clone()); self.log("Error", "Failover", &error); }
                }
            }
        }
    }

    fn get_logs(&self) -> Value {
        let records = self.logs.lock().map(|q| q.iter().map(|e| json!({
            "timestamp": e.timestamp.to_rfc3339(), "level": e.level, "source": e.source, "message": e.message
        })).collect::<Vec<_>>()).unwrap_or_default();
        json!(records)
    }

    fn diagnostics(&mut self) -> Value {
        let adapters = network::get_adapters(&self.settings).unwrap_or_else(|_| json!([]));
        let active_policies = self.policies().as_array().cloned().unwrap_or_default().into_iter().filter(|p| get_bool(p, "enabled", true)).count();
        let rules = array_at(&self.settings, "applicationRules").as_array().map_or(0, Vec::len);
        let generated_rules = rules + active_policies.max(1);
        let mut result = json!({
            "healthProbes": self.probe_count, "successfulProbes": self.successful_probes,
            "failedProbes": self.failed_probes, "failoverCount": self.failovers, "failbackCount": self.failbacks,
            "rulesCount": rules, "policiesCount": self.policies().as_array().map_or(0, Vec::len),
            "healthContexts": adapters.as_array().map_or(0, Vec::len) * if get_bool(&self.settings, "ipv6Enabled", true) { 2 } else { 1 },
            "servicePrivateBytes": 0, "serviceWorkingSetBytes": 0, "serviceCpuSeconds": 0.0,
            "coreRunning": self.core.is_running(), "routingEnabled": get_bool(&self.settings, "enabled", false),
            "singBoxPrivateBytes": self.core.private_bytes(),
            "singBoxWorkingSetBytes": self.core.working_set_bytes(),
            "singBoxCpuSeconds": self.core.cpu_seconds(),
            "mihomoPrivateBytes": self.core.private_bytes(),
            "mihomoWorkingSetBytes": self.core.working_set_bytes(),
            "mihomoCpuSeconds": self.core.cpu_seconds(),
            "singBoxVersion": self.core.version(),
            "uiPrivateBytes": null, "uiWorkingSetBytes": null, "uiCpuSeconds": null,
            "generatedRouteRules": generated_rules,
            "generatedSelectors": active_policies * if get_bool(&self.settings, "ipv6Enabled", true) { 2 } else { 1 },
            "generatedDirectOutbounds": adapters.as_array().map_or(0, Vec::len)
        });
        result["mihomoManagedProcess"] = json!(self.core.process_id());
        result
    }

    fn connection_telemetry(&mut self) -> Result<Value, String> {
        let snapshot = if self.core.is_running() { self.core.connections()? } else { json!({"connections": [], "uploadTotal": 0, "downloadTotal": 0}) };
        let adapters = network::get_adapters(&self.settings)?;
        let mut by_tag = HashMap::<String, (String, String)>::new();
        for adapter in adapters.as_array().into_iter().flatten() {
            let id = get_str(adapter, "id").unwrap_or_default();
            let name = get_str(adapter, "name").unwrap_or_default();
            by_tag.insert(mihomo::outbound_tag(&id), (id, name));
        }
        let mut connections_out = Vec::new();
        let mut active_counts: HashMap<String, i64> = HashMap::new();
        let mut active_ids = Vec::new();
        let conns = snapshot.get("connections").and_then(Value::as_array).cloned().unwrap_or_default();
        for conn in conns {
            let metadata = conn.get("metadata").cloned().unwrap_or(Value::Null);
            let id = get_str(&conn, "id").unwrap_or_default();
            let upload = conn.get("upload").and_then(Value::as_u64).unwrap_or(0);
            let download = conn.get("download").and_then(Value::as_u64).unwrap_or(0);
            let chains = conn.get("chains").and_then(Value::as_array).cloned().unwrap_or_default();
            let actual = chains.iter().filter_map(Value::as_str).find_map(|tag| by_tag.get(tag).cloned());
            let (interface_id, actual_name) = actual.unwrap_or_default();
            if !interface_id.is_empty() {
                *active_counts.entry(interface_id.clone()).or_default() += 1;
                if !id.is_empty() {
                    let previous = self.traffic_seen.get(&id).cloned();
                    let delta_up = previous.as_ref().filter(|v| v.0 == interface_id).map(|v| upload.saturating_sub(v.1)).unwrap_or(upload);
                    let delta_down = previous.as_ref().filter(|v| v.0 == interface_id).map(|v| download.saturating_sub(v.2)).unwrap_or(download);
                    let total = self.traffic_totals.entry(interface_id.clone()).or_default();
                    total.0 = total.0.saturating_add(delta_up);
                    total.1 = total.1.saturating_add(delta_down);
                    self.traffic_seen.insert(id.clone(), (interface_id.clone(), upload, download));
                    active_ids.push(id.clone());
                }
            }
            let process_path = get_str(&metadata, "processPath").unwrap_or_default();
            let process = get_str(&metadata, "process").unwrap_or_else(|_| Path::new(&process_path).file_name().and_then(|n| n.to_str()).unwrap_or("Unknown").to_owned());
            connections_out.push(json!({
                "id": id, "process": process, "processPath": process_path,
                "destinationIp": get_str(&metadata, "destinationIP").unwrap_or_default(),
                "destinationHost": get_str(&metadata, "destinationHost").unwrap_or_default(),
                "destinationPort": string_or_number(&metadata, "destinationPort"),
                "network": get_str(&metadata, "network").unwrap_or_default(),
                "actualOutbound": if actual_name.is_empty() { "Unknown" } else { &actual_name },
                "predictedOutbound": "Unknown", "uploadBytes": upload, "downloadBytes": download,
                "startedAt": get_str(&conn, "start").ok()
            }));
        }
        self.traffic_seen.retain(|id, _| active_ids.contains(id));
        let mut weighted_stats: HashMap<String, Value> = HashMap::new();
        if get_bool(&self.settings["defaultPolicy"], "loadBalanceEnabled", false) {
            let policy_id = get_str(&self.settings["defaultPolicy"], "id").unwrap_or_default();
            if let Ok(group) = self.core.weighted_stats(&policy_id, "IPv4") {
                if let Some(stats) = group.get("byteStats").and_then(Value::as_object) {
                    for (name, row) in stats { weighted_stats.insert(name.clone(), row.clone()); }
                }
            }
        }
        let mut outbound_rows = Vec::new();
        for adapter in adapters.as_array().into_iter().flatten() {
            let id = get_str(adapter, "id").unwrap_or_default();
            let name = get_str(adapter, "name").unwrap_or_default();
            let total = self.traffic_totals.get(&id).cloned().unwrap_or_default();
            let stats = weighted_stats.get(&mihomo::outbound_tag(&id));
            outbound_rows.push(json!({
                "interfaceId": id, "name": name, "uploadBytes": total.0, "downloadBytes": total.1,
                "activeConnections": active_counts.get(&id).copied().unwrap_or(0),
                "available": get_bool(adapter, "isUserAllowed", false),
                "targetPercent": stats.and_then(|v| v.get("targetShare")).and_then(Value::as_f64).map(|n| n.round() as i64),
                "rollingBytes": stats.and_then(|v| v.get("rollingBytes")).and_then(Value::as_u64),
                "rollingUploadBytes": stats.and_then(|v| v.get("rollingUploadBytes")).and_then(Value::as_u64),
                "rollingDownloadBytes": stats.and_then(|v| v.get("rollingDownloadBytes")).and_then(Value::as_u64),
                "actualShare": stats.and_then(|v| v.get("actualShare")).and_then(Value::as_f64),
                "targetShare": stats.and_then(|v| v.get("targetShare")).and_then(Value::as_f64),
                "effectiveWeight": stats.and_then(|v| v.get("effectiveWeight")).and_then(Value::as_f64),
                "healthy": stats.and_then(|v| v.get("healthy")).and_then(Value::as_bool),
                "warmingUp": stats.and_then(|v| v.get("warmingUp")).and_then(Value::as_bool)
            }));
        }
        Ok(json!({
            "sampledAt": Utc::now().to_rfc3339(), "lastError": self.last_error,
            "uploadTotal": snapshot.get("uploadTotal").and_then(Value::as_u64).unwrap_or(0),
            "downloadTotal": snapshot.get("downloadTotal").and_then(Value::as_u64).unwrap_or(0),
            "outbounds": outbound_rows, "connections": connections_out
        }))
    }

    fn save_rule(&mut self, mut rule: Value) -> Result<Value, String> {
        if get_str(&rule, "processName").unwrap_or_default().is_empty() && get_str(&rule, "executablePath").unwrap_or_default().is_empty() {
            return Err("Choose an executable path or process name.".into());
        }
        if !self.policies().as_array().into_iter().flatten().any(|p| get_str(p, "id").unwrap_or_default() == get_str(&rule, "policyId").unwrap_or_default()) {
            return Err("The selected policy does not exist.".into());
        }
        if get_str(&rule, "id").unwrap_or_default().is_empty() { rule["id"] = json!(uuid_like()); }
        rule["updatedAt"] = json!(Utc::now().to_rfc3339());
        let mut next = self.settings.clone();
        upsert_value(&mut next, "applicationRules", rule)?;
        self.apply_draft(next)
    }

    fn save_policy(&mut self, mut policy: Value) -> Result<Value, String> {
        if get_str(&policy, "id").unwrap_or_default().is_empty() { policy["id"] = json!(uuid_like()); }
        let mut next = self.settings.clone();
        if next.get("defaultPolicy").and_then(|p| get_str(p, "id").ok()) == get_str(&policy, "id").ok() {
            next["defaultPolicy"] = policy;
        } else { upsert_value(&mut next, "policies", policy)?; }
        self.apply_draft(next)
    }

    fn delete_policy(&mut self, id: &str) -> Result<Value, String> {
        if self.settings.get("defaultPolicy").and_then(|p| get_str(p, "id").ok()).as_deref() == Some(id) {
            return Err("Default policy cannot be deleted.".into());
        }
        if array_at(&self.settings, "applicationRules").as_array().into_iter().flatten().any(|r| get_str(r, "policyId").unwrap_or_default() == id) {
            return Err("Policy is in use by an application rule.".into());
        }
        let mut next = self.settings.clone();
        delete_value(&mut next, "policies", id)?;
        self.apply_draft(next)
    }

    fn set_default_policy(&mut self, id: &str) -> Result<Value, String> {
        let mut next = self.settings.clone();
        let items = next.get_mut("policies").and_then(Value::as_array_mut).ok_or("Policy list is invalid.")?;
        let index = items.iter().position(|p| get_str(p, "id").as_deref() == Ok(id)).ok_or("Policy not found.")?;
        let selected = items.remove(index);
        drop(items);
        let old = std::mem::replace(&mut next["defaultPolicy"], selected);
        next.get_mut("policies").and_then(Value::as_array_mut).ok_or("Policy list is invalid.")?.push(old);
        self.apply_draft(next)
    }

    fn set_traffic_ratio(&mut self, percent: i64) -> Result<Value, String> {
        if !(1..=99).contains(&percent) { return Err("The primary traffic target must be between 1 and 99 percent.".into()); }
        let mut next = self.settings.clone();
        next["defaultPolicy"]["primaryTrafficPercent"] = json!(percent);
        if get_bool(&self.settings["defaultPolicy"], "loadBalanceEnabled", false) && self.core.is_running() {
            let primary = get_str(&self.settings["defaultPolicy"], "primaryInterfaceId")?;
            let fallback = get_str(&self.settings["defaultPolicy"], "fallbackInterfaceId")?;
            let weights = json!({mihomo::outbound_tag(&primary): percent, mihomo::outbound_tag(&fallback): 100 - percent});
            self.core.set_weights(&get_str(&self.settings["defaultPolicy"], "id")?, "IPv4", weights.clone())?;
            if get_bool(&self.settings, "ipv6Enabled", true) { self.core.set_weights(&get_str(&self.settings["defaultPolicy"], "id")?, "IPv6", weights)?; }
            self.settings = next;
            self.persist_settings()?;
            return Ok(Value::Null);
        }
        self.apply_draft(next)
    }

    fn save_settings(&mut self, settings: Value) -> Result<Value, String> {
        let mut next = settings;
        merge_defaults(&mut next, default_settings());
        self.validate_settings(&next)?;
        self.apply_draft(next)
    }

    fn set_interface_usability(&mut self, payload: Value) -> Result<Value, String> {
        let id = get_str(&payload, "interfaceId")?;
        let allowed = get_bool(&payload, "allowed", false);
        let mut next = self.settings.clone();
        if !next.get("interfaceUsabilityOverrides").is_some_and(Value::is_object) {
            next["interfaceUsabilityOverrides"] = json!({});
        }
        next["interfaceUsabilityOverrides"][id] = json!(allowed);
        self.apply_draft(next)
    }

    fn set_routing(&mut self, enabled: bool) -> Result<Value, String> {
        let mut next = self.settings.clone();
        next["enabled"] = json!(enabled);
        self.apply_draft(next)
    }

    fn restart_core(&mut self) -> Result<Value, String> {
        let adapters = network::get_adapters(&self.settings)?;
        self.core.stop();
        self.start_core(&adapters)?;
        Ok(Value::Null)
    }

    fn validate_config(&mut self) -> Result<Value, String> {
        let adapters = network::get_adapters(&self.settings)?;
        self.validate_settings(&self.settings.clone())?;
        self.core.validate(&self.settings, &adapters)?;
        Ok(Value::Null)
    }

    fn test_interface(&mut self, payload: Value) -> Result<Value, String> {
        let id = get_str(&payload, "interfaceId")?;
        let family = get_str(&payload, "family")?;
        let adapters = network::get_adapters(&self.settings)?;
        let adapter = adapters.as_array().and_then(|a| a.iter().find(|v| get_str(v, "id").as_deref() == Ok(id.as_str()))).ok_or("Interface is no longer available.")?;
        if !get_bool(adapter, "isUserAllowed", false) { return Err("Allow this interface before testing it.".into()); }
        let latency = network::probe_adapter(adapter, &family, Duration::from_secs(3));
        self.probe_count += 1;
        if latency.is_some() { self.successful_probes += 1 } else { self.failed_probes += 1 }
        let health = if latency.is_some() { "Healthy" } else { "Down" };
        let record = self.adapter_health.entry((id.clone(), family.clone())).or_default();
        record.state = health.into(); record.latency_ms = latency; record.last_probe = Some(Utc::now());
        Ok(json!({"interfaceId": id, "family": family, "health": health, "latencyMs": latency}))
    }

    fn export_diagnostics(&mut self, payload: Value) -> Result<Value, String> {
        let redact = get_bool(&payload, "redactIpAddresses", false);
        let filename = format!("EasyNetBalance-{}.zip", Utc::now().format("%Y%m%d-%H%M%S"));
        let mut cursor = Cursor::new(Vec::<u8>::new());
        {
            let mut zip = zip::ZipWriter::new(&mut cursor);
            let options = zip::write::SimpleFileOptions::default().compression_method(zip::CompressionMethod::Deflated);
            let mut metadata = json!({"easyNetBalanceVersion":"0.2.0","osVersion":std::env::consts::OS,"mihomoVersion":self.core.version()});
            if redact { metadata["redacted"] = json!(true); }
            add_zip_json(&mut zip, "metadata.json", &metadata, options)?;
            add_zip_json(&mut zip, "status.json", &self.status(), options)?;
            add_zip_json(&mut zip, "diagnostics.json", &self.diagnostics(), options)?;
            if !redact { add_zip_json(&mut zip, "settings.json", &self.settings, options)?; }
            let mut adapters = network::get_adapters(&self.settings)?;
            if redact {
                if let Some(rows) = adapters.as_array_mut() {
            for row in rows { for field in ["ipv4Addresses", "ipv6Addresses", "ipv4Gateways", "ipv6Gateways", "dnsServers"] { if row.get(field).is_some() { row[field] = json!(["[redacted]"]); } } }
                }
            }
            add_zip_json(&mut zip, "adapters.json", &adapters, options)?;
            if !redact { add_zip_json(&mut zip, "logs.json", &self.get_logs(), options)?; }
            if !redact {
                let config = self.core.redacted_config()?;
                if !config.is_empty() { zip.start_file("mihomo.redacted.yaml", options).map_err(|e| e.to_string())?; zip.write_all(config.as_bytes()).map_err(|e| e.to_string())?; }
            }
            zip.finish().map_err(|e| e.to_string())?;
        }
        Ok(json!({"fileName": filename, "dataBase64": base64::engine::general_purpose::STANDARD.encode(cursor.into_inner())}))
    }

    fn apply_current(&mut self) -> Result<Value, String> {
        self.validate_settings(&self.settings.clone())?;
        if get_bool(&self.settings, "enabled", false) {
            let adapters = network::get_adapters(&self.settings)?;
            self.core.apply(&self.settings, &adapters)?;
            self.started_at = Some(Instant::now());
            self.active_routes.clear();
            for policy in self.policies().as_array().into_iter().flatten() {
                let id = get_str(policy, "id").unwrap_or_default();
                let primary = get_str(policy, "primaryInterfaceId").unwrap_or_default();
                for family in ["IPv4", "IPv6"] { self.active_routes.insert((id.clone(), family.to_owned()), primary.clone()); }
            }
        } else { self.core.stop(); self.started_at = None; }
        self.persist_settings()?;
        self.log("Information", "Settings", "Configuration saved.");
        Ok(Value::Null)
    }

    fn apply_draft(&mut self, next: Value) -> Result<Value, String> {
        let previous = std::mem::replace(&mut self.settings, next);
        match self.apply_current() {
            Ok(value) => Ok(value),
            Err(error) => {
                self.settings = previous;
                if get_bool(&self.settings, "enabled", false) {
                    if let Ok(adapters) = network::get_adapters(&self.settings) { let _ = self.core.apply(&self.settings, &adapters); }
                } else { self.core.stop(); }
                Err(error)
            }
        }
    }

    fn start_core(&mut self, adapters: &Value) -> Result<(), String> {
        self.core.start(&self.settings, adapters)?;
        self.started_at = Some(Instant::now());
        self.last_error = None;
        Ok(())
    }

    fn validate_settings(&self, settings: &Value) -> Result<(), String> {
        let policy = settings.get("defaultPolicy").ok_or("A default routing policy must be configured.")?;
        let primary = get_str(policy, "primaryInterfaceId").unwrap_or_default();
        if get_bool(policy, "loadBalanceEnabled", false) {
            let fallback = get_str(policy, "fallbackInterfaceId").unwrap_or_default();
            if primary.is_empty() || fallback.is_empty() || primary.eq_ignore_ascii_case(&fallback) {
                return Err("Byte-weighted routing requires two different default-policy interfaces.".into());
            }
            let percent = get_i64(policy, "primaryTrafficPercent").unwrap_or(-1);
            if !(0..=100).contains(&percent) { return Err("The primary traffic target must be between 0 and 100 percent.".into()); }
        }
        let mut ids = std::collections::HashSet::new();
        for p in self.policies().as_array().into_iter().flatten() {
            let id = get_str(p, "id").unwrap_or_default().to_lowercase();
            if id.is_empty() || !ids.insert(id) { return Err("Policy IDs must be nonempty and unique, ignoring case.".into()); }
            if get_i64(p, "failureThreshold").unwrap_or(3) < 1 || get_i64(p, "recoveryThreshold").unwrap_or(3) < 1 {
                return Err("Policy health thresholds must be greater than zero.".into());
            }
        }
        if primary.is_empty() { return Err("Select a primary network interface for the default policy.".into()); }
        if let Some(endpoints) = settings.get("probeEndpoints").and_then(Value::as_array) {
            for endpoint in endpoints.iter().filter(|e| get_bool(e, "enabled", true)) {
                let url = get_str(endpoint, "url")?;
                if !url.starts_with("https://") { return Err("Probe endpoints must use HTTPS.".into()); }
            }
        }
        Ok(())
    }

    fn active_target(&self, policy_id: &str, family: &str) -> Option<String> {
        self.active_routes.get(&(policy_id.to_owned(), family.to_owned())).cloned()
    }

    fn delete_item(&mut self, list: &str, id: &str) -> Result<Value, String> {
        let mut next = self.settings.clone();
        delete_value(&mut next, list, id)?;
        self.apply_draft(next)
    }

    fn persist_settings(&self) -> Result<(), String> {
        let path = self.data_dir.join("settings.json");
        let temp = self.data_dir.join("settings.json.tmp");
        fs::write(&temp, serde_json::to_vec_pretty(&self.settings).map_err(|e| e.to_string())?).map_err(|e| e.to_string())?;
        replace_file(&temp, &path).map_err(|e| format!("Could not save settings: {e}"))
    }

    fn log(&self, level: &str, source: &str, message: &str) {
        if let Ok(mut records) = self.logs.lock() {
            records.push_back(LogEntry { timestamp: Utc::now(), level: level.to_owned(), source: source.to_owned(), message: message.to_owned() });
            while records.len() > 500 { records.pop_front(); }
        }
    }
}

pub(super) fn handle_request(runtime: &Arc<Mutex<Runtime>>, request: Value) -> Value {
    let method = get_str(&request, "method").unwrap_or_default();
    let payload = request.get("payload").cloned().unwrap_or(Value::Null);
    let result = runtime.lock().map_err(|_| "The service runtime is unavailable.".to_owned()).and_then(|mut r| r.handle(&method, payload));
    match result {
        Ok(payload) => json!({"success": true, "error": null, "payload": payload}),
        Err(error) => json!({"success": false, "error": error, "payload": null}),
    }
}

fn data_directory() -> PathBuf {
    std::env::var_os("PROGRAMDATA").map(PathBuf::from).unwrap_or_else(|| PathBuf::from(r"C:\ProgramData")).join(PRODUCT_NAME)
}

fn secure_data_directory(_path: &Path) -> Result<(), String> {
    // The installer owns the ProgramData DACL and grants only SYSTEM and Administrators.
    // Refuse reparse points so settings and generated configuration cannot be redirected.
    let metadata = fs::symlink_metadata(_path).map_err(|e| e.to_string())?;
    if metadata.file_type().is_symlink() { return Err("The EasyNetBalance data directory cannot be a reparse point.".into()); }
    #[cfg(windows)] {
        use std::os::windows::ffi::OsStrExt;
        use std::ffi::c_void;
        #[link(name = "advapi32")]
        extern "system" {
            fn ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl: *const u16, revision: u32, descriptor: *mut *mut c_void, size: *mut u32) -> i32;
            fn SetFileSecurityW(path: *const u16, information: u32, descriptor: *mut c_void) -> i32;
        }
        #[link(name = "kernel32")]
        extern "system" { fn LocalFree(memory: *mut c_void) -> *mut c_void; }
        let path: Vec<u16> = _path.as_os_str().encode_wide().chain(Some(0)).collect();
        let sddl: Vec<u16> = "D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)".encode_utf16().chain(Some(0)).collect();
        let mut descriptor = std::ptr::null_mut();
        if unsafe { ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.as_ptr(), 1, &mut descriptor, std::ptr::null_mut()) } == 0 {
            return Err(format!("Could not create service data ACL: {}", std::io::Error::last_os_error()));
        }
        let okay = unsafe { SetFileSecurityW(path.as_ptr(), 0x0000_0004 | 0x8000_0000, descriptor) };
        unsafe { LocalFree(descriptor); }
        if okay == 0 { return Err(format!("Could not protect service data directory: {}", std::io::Error::last_os_error())); }
    }
    Ok(())
}

fn add_zip_json<W: Write + std::io::Seek>(zip: &mut zip::ZipWriter<W>, name: &str, value: &Value, options: zip::write::SimpleFileOptions) -> Result<(), String> {
    zip.start_file(name, options).map_err(|e| e.to_string())?;
    zip.write_all(&serde_json::to_vec_pretty(value).map_err(|e| e.to_string())?).map_err(|e| e.to_string())
}

fn array_at(value: &Value, key: &str) -> Value { value.get(key).cloned().filter(Value::is_array).unwrap_or_else(|| json!([])) }
fn setting_seconds(value: &Value, key: &str, fallback: u64) -> u64 {
    let raw = value.get(key).and_then(Value::as_str).unwrap_or_default();
    if let Some(seconds) = raw.strip_prefix("PT").and_then(|text| text.strip_suffix('S')).and_then(|text| text.parse::<u64>().ok()) { return seconds; }
    let parts: Vec<&str> = raw.split(':').collect();
    if parts.len() == 3 {
        let hours = parts[0].parse::<u64>().ok(); let minutes = parts[1].parse::<u64>().ok();
        let seconds = parts[2].split('.').next().and_then(|text| text.parse::<u64>().ok());
        if let (Some(h), Some(m), Some(s)) = (hours, minutes, seconds) { return h.saturating_mul(3600).saturating_add(m.saturating_mul(60)).saturating_add(s); }
    }
    fallback
}
fn string_or_number(value: &Value, key: &str) -> String {
    value.get(key).map(|v| if let Some(s) = v.as_str() { s.to_owned() } else if let Some(n) = v.as_u64() { n.to_string() } else { String::new() }).unwrap_or_default()
}
fn upsert_value(settings: &mut Value, list: &str, item: Value) -> Result<(), String> {
    let id = get_str(&item, "id")?;
    let rows = settings.get_mut(list).and_then(Value::as_array_mut).ok_or("Settings list is invalid.")?;
    if let Some(index) = rows.iter().position(|v| get_str(v, "id").as_deref() == Ok(id.as_str())) { rows[index] = item; }
    else { rows.push(item); }
    Ok(())
}
fn delete_value(settings: &mut Value, list: &str, id: &str) -> Result<(), String> {
    let rows = settings.get_mut(list).and_then(Value::as_array_mut).ok_or("Settings list is invalid.")?;
    rows.retain(|value| get_str(value, "id").unwrap_or_default() != id);
    Ok(())
}

fn replace_file(source: &Path, destination: &Path) -> Result<(), String> {
    #[cfg(windows)] {
        use std::os::windows::ffi::OsStrExt;
        #[link(name = "kernel32")]
        extern "system" { fn MoveFileExW(existing: *const u16, new: *const u16, flags: u32) -> i32; }
        let from: Vec<u16> = source.as_os_str().encode_wide().chain(Some(0)).collect();
        let to: Vec<u16> = destination.as_os_str().encode_wide().chain(Some(0)).collect();
        if unsafe { MoveFileExW(from.as_ptr(), to.as_ptr(), 0x1 | 0x8) } == 0 { return Err(std::io::Error::last_os_error().to_string()); }
        Ok(())
    }
    #[cfg(not(windows))] { fs::rename(source, destination).map_err(|e| e.to_string()) }
}
fn uuid_like() -> String { format!("{}-{}-4{}-a{}-{}", Utc::now().timestamp(), std::process::id(), Utc::now().timestamp_subsec_nanos() % 10, std::process::id() % 10, rand::random::<u64>()) }
fn format_timespan(duration: Duration) -> String {
    let seconds = duration.as_secs();
    let hours = seconds / 3600; let minutes = seconds % 3600 / 60; let secs = seconds % 60;
    if duration.subsec_nanos() == 0 { format!("{hours:02}:{minutes:02}:{secs:02}") }
    else { format!("{hours:02}:{minutes:02}:{secs:02}.{:07}", duration.subsec_nanos() / 100) }
}
