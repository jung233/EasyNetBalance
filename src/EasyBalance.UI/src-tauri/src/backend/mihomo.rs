use chrono::Utc;
use base64::Engine;
use rand::RngCore;
use serde_json::{json, Value};
use serde_yaml::{Mapping, Value as YamlValue};
use std::collections::HashMap;
use std::fs;
use std::io::{BufRead, BufReader};
use std::net::TcpListener;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::collections::VecDeque;
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{Duration, Instant};

use super::models::{get_bool, get_str};
use super::models::get_i64;
use super::LogEntry;

pub(super) struct MihomoCore {
    data_dir: PathBuf,
    binary: PathBuf,
    config_path: PathBuf,
    child: Option<Child>,
    controller: Option<String>,
    secret: Option<String>,
    version: Option<String>,
    last_exit_code: Option<i32>,
    started_at: Option<Instant>,
    generated_yaml: String,
    logs: Arc<Mutex<VecDeque<LogEntry>>>,
}

impl MihomoCore {
    pub(super) fn new(data_dir: PathBuf, logs: Arc<Mutex<VecDeque<LogEntry>>>) -> Self {
        let working = data_dir.join("mihomo");
        let binary = std::env::current_exe().ok().and_then(|path| path.parent().map(|p| p.join("resources").join("core").join("mihomo.exe")))
            .unwrap_or_else(|| PathBuf::from(r"C:\Program Files\EasyNetBalance\resources\core\mihomo.exe"));
        Self { config_path: working.join("config.yaml"), data_dir: working, binary, child: None, controller: None, secret: None, version: None, last_exit_code: None, started_at: None, generated_yaml: String::new(), logs }
    }

    pub(super) fn is_running(&self) -> bool { self.child.is_some() }
    pub(super) fn version(&self) -> Option<String> { self.version.clone() }
    pub(super) fn last_exit_code(&self) -> Option<i32> { self.last_exit_code }
    pub(super) fn process_id(&self) -> Option<u32> { self.child.as_ref().map(Child::id) }
    pub(super) fn private_bytes(&self) -> Option<u64> { self.process_memory().map(|m| m.0) }
    pub(super) fn working_set_bytes(&self) -> Option<u64> { self.process_memory().map(|m| m.1) }
    pub(super) fn cpu_seconds(&self) -> Option<f64> { None }
    pub(super) fn supports_weighted_bytes(&self) -> bool {
        self.api_json("GET", "/features", None).ok().is_some_and(|value| super::models::get_bool(&value, "weightedBytes", false))
    }

    fn process_memory(&self) -> Option<(u64, u64)> {
        let pid = self.process_id()?;
        let mut system = sysinfo::System::new();
        let pid = sysinfo::Pid::from_u32(pid);
        system.refresh_processes(sysinfo::ProcessesToUpdate::Some(&[pid]), true);
        let process = system.process(pid)?;
        Some((process.memory() * 1024, process.memory() * 1024))
    }

    pub(super) fn start(&mut self, settings: &Value, adapters: &Value) -> Result<(), String> {
        if self.child.is_some() { self.stop(); }
        fs::create_dir_all(&self.data_dir).map_err(|e| format!("Could not create Mihomo working directory: {e}"))?;
        if !self.binary.is_file() { return Err(format!("Mihomo core was not found at {}.", self.binary.display())); }
        self.version = Command::new(&self.binary).arg("-v").output().ok().filter(|o| o.status.success()).map(|o| String::from_utf8_lossy(&o.stdout).trim().to_owned());
        let (config, controller, secret) = generate_config(settings, adapters)?;
        self.generated_yaml = config;
        self.controller = Some(controller);
        self.secret = Some(secret);
        self.write_config()?;
        self.validate_binary_config()?;
        let mut command = Command::new(&self.binary);
        command.arg("-d").arg(&self.data_dir).arg("-f").arg(&self.config_path)
            .current_dir(&self.data_dir).stdin(Stdio::null()).stdout(Stdio::piped()).stderr(Stdio::piped());
        let mut child = command.spawn().map_err(|e| format!("Could not start Mihomo at {} using working directory {}: {e}", self.binary.display(), self.data_dir.display()))?;
        self.attach_log_reader(child.stdout.take(), "Mihomo");
        self.attach_log_reader(child.stderr.take(), "Mihomo");
        self.child = Some(child);
        self.started_at = Some(Instant::now());
        if let Err(error) = self.wait_ready(Duration::from_secs(12)) {
            self.stop();
            return Err(error);
        }
        self.last_exit_code = None;
        Ok(())
    }

    pub(super) fn apply(&mut self, settings: &Value, adapters: &Value) -> Result<(), String> {
        self.start(settings, adapters)
    }

    pub(super) fn stop(&mut self) {
        if let Some(mut child) = self.child.take() {
            let _ = child.kill();
            if let Ok(status) = child.wait() { self.last_exit_code = status.code(); }
        }
        self.started_at = None;
        self.controller = None;
        self.secret = None;
    }

    pub(super) fn validate(&mut self, settings: &Value, adapters: &Value) -> Result<(), String> {
        fs::create_dir_all(&self.data_dir).map_err(|e| e.to_string())?;
        if !self.binary.is_file() { return Err(format!("Mihomo core was not found at {}.", self.binary.display())); }
        let (config, _, _) = generate_config(settings, adapters)?;
        let old = std::mem::replace(&mut self.generated_yaml, config);
        let result = self.write_config().and_then(|_| self.validate_binary_config());
        self.generated_yaml = old;
        result
    }

    pub(super) fn redacted_config(&self) -> Result<String, String> {
        if self.generated_yaml.is_empty() { return Ok(String::new()); }
        let mut yaml: YamlValue = serde_yaml::from_str(&self.generated_yaml).map_err(|e| e.to_string())?;
        if let Some(map) = yaml.as_mapping_mut() { map.insert(YamlValue::String("secret".into()), YamlValue::String("[redacted]".into())); }
        serde_yaml::to_string(&yaml).map_err(|e| e.to_string())
    }

    pub(super) fn connections(&mut self) -> Result<Value, String> { self.api_json("GET", "/connections", None) }

    pub(super) fn weighted_stats(&self, policy_id: &str, family: &str) -> Result<Value, String> {
        self.api_json("GET", &format!("/proxies/{}", selector_tag(policy_id, family)), None)
    }

    pub(super) fn set_weights(&self, policy_id: &str, family: &str, weights: Value) -> Result<(), String> {
        let url = format!("/proxies/{}", selector_tag(policy_id, family));
        let expected = weights.as_object().cloned().ok_or("Weight payload must be an object.")?;
        self.api_json("PUT", &url, Some(json!({"weights": weights})))?;
        let current = self.api_json("GET", &url, None)?;
        let configured = current.get("weights").ok_or("Mihomo did not return updated weighted-byte targets.")?;
        for (name, value) in &expected {
            if configured.get(name) != Some(value) { return Err(format!("Mihomo did not confirm the target weight for {name}.")); }
        }
        Ok(())
    }

    pub(super) fn switch_selector(&mut self, policy_id: &str, family: &str, outbound: &str) -> Result<(), String> {
        let group = selector_tag(policy_id, family);
        let url = format!("/proxies/{}", group);
        let current_group = self.api_json("GET", &url, None)?;
        if current_group.get("type").and_then(Value::as_str).is_some_and(|kind| kind.eq_ignore_ascii_case("loadbalance") || kind.eq_ignore_ascii_case("load-balance")) {
            let members = current_group.get("all").and_then(Value::as_array).ok_or("Mihomo weighted group returned no members.")?;
            let mut weights = serde_json::Map::new();
            for member in members.iter().filter_map(Value::as_str) { weights.insert(member.to_owned(), json!(if member == outbound { 100 } else { 0 })); }
            self.api_json("PUT", &url, Some(json!({"weights": weights})))?;
            return Ok(());
        }
        self.api_json("PUT", &url, Some(json!({"name": outbound})))?;
        let current = self.api_json("GET", &url, None)?;
        let active = get_str(&current, "now").unwrap_or_default();
        if active != outbound { return Err(format!("Mihomo did not confirm selector {group} -> {outbound}.")); }
        Ok(())
    }

    fn wait_ready(&mut self, timeout: Duration) -> Result<(), String> {
        let deadline = Instant::now() + timeout;
        while Instant::now() < deadline {
            if let Some(child) = self.child.as_mut() {
                if let Some(status) = child.try_wait().map_err(|e| e.to_string())? {
                    self.last_exit_code = status.code();
                    self.child = None;
                    return Err(format!("Mihomo exited during startup with code {:?}.", self.last_exit_code));
                }
            }
            if self.api_json("GET", "/version", None).is_ok() { return Ok(()); }
            thread::sleep(Duration::from_millis(200));
        }
        Err("Mihomo control API did not become ready within 12 seconds.".into())
    }

    fn api_json(&self, method: &str, path: &str, body: Option<Value>) -> Result<Value, String> {
        let base = self.controller.as_ref().ok_or("Mihomo is not running.")?;
        let secret = self.secret.as_ref().ok_or("Mihomo API secret is unavailable.")?;
        let client = reqwest::blocking::Client::builder().timeout(Duration::from_secs(5)).build().map_err(|e| e.to_string())?;
        let url = format!("http://{base}{path}");
        let mut request = match method { "GET" => client.get(url), "PUT" => client.put(url), "DELETE" => client.delete(url), _ => return Err("Unsupported Mihomo API method.".into()) };
        request = request.bearer_auth(secret);
        if let Some(body) = body { request = request.json(&body); }
        let response = request.send().map_err(|e| format!("Mihomo control API request failed: {e}"))?;
        let status = response.status();
        let text = response.text().map_err(|e| e.to_string())?;
        if !status.is_success() { return Err(format!("Mihomo control API returned {status}: {text}")); }
        if text.trim().is_empty() { Ok(Value::Null) }
        else { serde_json::from_str(&text).map_err(|e| format!("Mihomo returned invalid JSON: {e}")) }
    }

    fn write_config(&self) -> Result<(), String> {
        let temp = self.config_path.with_extension("yaml.tmp");
        fs::write(&temp, &self.generated_yaml).map_err(|e| format!("Could not write Mihomo config: {e}"))?;
        replace_file(&temp, &self.config_path)
    }

    fn validate_binary_config(&self) -> Result<(), String> {
        let output = Command::new(&self.binary).arg("-t").arg("-d").arg(&self.data_dir).arg("-f").arg(&self.config_path)
            .current_dir(&self.data_dir).output().map_err(|e| format!("Could not validate Mihomo configuration using {} and working directory {}: {e}", self.binary.display(), self.data_dir.display()))?;
        if output.status.success() { Ok(()) }
        else {
            let details = String::from_utf8_lossy(&output.stderr);
            let stdout = String::from_utf8_lossy(&output.stdout);
            Err(format!("Mihomo rejected the generated configuration: {}{}", details.trim(), stdout.trim()))
        }
    }

    fn attach_log_reader(&self, stream: Option<impl std::io::Read + Send + 'static>, source: &'static str) {
        if let Some(stream) = stream {
            let logs = self.logs.clone();
            thread::spawn(move || {
                let reader = BufReader::new(stream);
                for line in reader.lines().flatten() {
                    if let Ok(mut records) = logs.lock() {
                        records.push_back(LogEntry { timestamp: Utc::now(), level: "Information".into(), source: source.into(), message: line });
                        while records.len() > 500 { records.pop_front(); }
                    }
                }
            });
        }
    }
}

impl Drop for MihomoCore { fn drop(&mut self) { self.stop(); } }

fn generate_config(settings: &Value, adapters: &Value) -> Result<(String, String, String), String> {
    let listener = TcpListener::bind(("127.0.0.1", 0)).map_err(|e| e.to_string())?;
    let port = listener.local_addr().map_err(|e| e.to_string())?.port(); drop(listener);
    let mut entropy = [0u8; 32]; rand::thread_rng().fill_bytes(&mut entropy);
    let secret = base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(entropy);
    let mut root = Mapping::new();
    put(&mut root, "mode", "rule"); put(&mut root, "log-level", "warning");
    put(&mut root, "ipv6", get_bool(settings, "ipv6Enabled", true)); put(&mut root, "allow-lan", false);
    put(&mut root, "find-process-mode", "strict"); put(&mut root, "external-controller", format!("127.0.0.1:{port}"));
    put(&mut root, "secret", secret.clone()); put(&mut root, "profile", mapping([("store-selected", YamlValue::Bool(true))]));
    put(&mut root, "tun", mapping([
        ("enable", YamlValue::Bool(true)), ("stack", strv("system")),
        ("dns-hijack", list([strv("any:53")])), ("auto-route", YamlValue::Bool(true)),
        ("strict-route", YamlValue::Bool(get_bool(settings, "strictRoute", true))),
        ("auto-detect-interface", YamlValue::Bool(true)),
    ]));

    let mut proxies = Vec::new();
    let mut adapter_by_id: HashMap<String, String> = HashMap::new();
    for adapter in adapters.as_array().into_iter().flatten().filter(|a| get_bool(a, "isUserAllowed", false)) {
        let id = get_str(adapter, "id")?;
        let name = get_str(adapter, "name")?;
        let tag = outbound_tag(&id);
        let mut proxy = Mapping::new(); put(&mut proxy, "name", tag.clone()); put(&mut proxy, "type", "direct"); put(&mut proxy, "interface-name", name.clone());
        proxies.push(YamlValue::Mapping(proxy));
        adapter_by_id.insert(id, tag);
    }
    if proxies.is_empty() { return Err("Allow at least one network interface before enabling routing.".into()); }
    put(&mut root, "proxies", YamlValue::Sequence(proxies));

    let policies = all_policies(settings);
    let mut groups = Vec::new(); let mut rules: Vec<String> = Vec::new();
    for policy in &policies {
        for family in ["IPv4", "IPv6"] {
            if family == "IPv6" && !get_bool(settings, "ipv6Enabled", true) { continue; }
            let group = selector_tag(&get_str(policy, "id")?, family);
            let mut candidates = Vec::new();
            let primary_id = get_str(policy, "primaryInterfaceId").unwrap_or_default();
            if let Some(tag) = adapter_by_id.get(&primary_id) { candidates.push(tag.clone()); }
            if get_bool(policy, "failoverEnabled", true) {
                if let Ok(id) = get_str(policy, "fallbackInterfaceId") { if let Some(tag) = adapter_by_id.get(&id) { if !candidates.contains(tag) { candidates.push(tag.clone()); } } }
            }
            if candidates.is_empty() { candidates.push(adapter_by_id.values().next().cloned().ok_or("No direct outbound is available.")?); }
            let mut group_map = Mapping::new(); put(&mut group_map, "name", group.clone());
            let weighted = get_bool(default_policy(settings), "loadBalanceEnabled", false) && get_str(policy, "id").ok() == get_str(default_policy(settings), "id").ok();
            if weighted && candidates.len() < 2 { return Err("Byte-weighted routing requires both configured interfaces to be available.".into()); }
            put(&mut group_map, "type", if weighted { "load-balance" } else { "select" });
            put(&mut group_map, "proxies", list(candidates.iter().map(|v| strv(v.clone()))));
            if weighted {
                put(&mut group_map, "strategy", "weighted-bytes");
                put(&mut group_map, "byte-window", "30s");
                let primary_percent = get_i64(policy, "primaryTrafficPercent").unwrap_or(50).max(0).min(100);
                let mut weights = Mapping::new();
                if candidates.len() >= 1 { weights.insert(strv(candidates[0].clone()), YamlValue::Number(primary_percent.into())); }
                if candidates.len() >= 2 { weights.insert(strv(candidates[1].clone()), YamlValue::Number((100 - primary_percent).into())); }
                put(&mut group_map, "weights", YamlValue::Mapping(weights));
            }
            groups.push(YamlValue::Mapping(group_map));
        }
    }

    let default_policy = settings.get("defaultPolicy").ok_or("A default routing policy must be configured.")?;
    for rule in settings.get("applicationRules").and_then(Value::as_array).into_iter().flatten().filter(|r| get_bool(r, "enabled", true)) {
        let policy_id = get_str(rule, "policyId").unwrap_or_default();
        let policy = policies.iter().find(|p| get_str(p, "id").unwrap_or_default() == policy_id).unwrap_or(default_policy);
        for family in ["IPv4", "IPv6"] {
            if family == "IPv6" && !get_bool(settings, "ipv6Enabled", true) { continue; }
            let network_rule = if family == "IPv4" { "IP-CIDR,0.0.0.0/0" } else { "IP-CIDR6,::/0" };
            let group = selector_tag(&get_str(policy, "id")?, family);
            if let Ok(path) = get_str(rule, "executablePath") {
                if path.contains(',') { return Err("Executable paths containing commas cannot be represented by Mihomo process rules.".into()); }
                rules.push(format!("AND,((PROCESS-PATH,{path}),({network_rule})),{group}"));
            } else if let Ok(name) = get_str(rule, "processName") {
                if name.contains(',') { return Err("Process names containing commas cannot be represented by Mihomo process rules.".into()); }
                rules.push(format!("AND,((PROCESS-NAME,{name}),({network_rule})),{group}"));
            }
        }
    }
    for family in ["IPv4", "IPv6"] {
        if family == "IPv6" && !get_bool(settings, "ipv6Enabled", true) { continue; }
        let group = selector_tag(&get_str(default_policy, "id")?, family);
        if family == "IPv4" { rules.push(format!("IP-CIDR,0.0.0.0/0,{group},no-resolve")); }
        else { rules.push(format!("IP-CIDR6,::/0,{group},no-resolve")); }
    }
    let default_v4 = selector_tag(&get_str(default_policy, "id")?, "IPv4");
    rules.push(format!("MATCH,{default_v4}"));
    put(&mut root, "proxy-groups", YamlValue::Sequence(groups));
    put(&mut root, "rules", list(rules.iter().map(|v| strv(v.clone()))));
    let yaml = serde_yaml::to_string(&YamlValue::Mapping(root)).map_err(|e| format!("Could not serialize Mihomo configuration: {e}"))?;
    Ok((yaml, format!("127.0.0.1:{port}"), secret))
}

fn all_policies(settings: &Value) -> Vec<Value> {
    let mut rows = Vec::new();
    if let Some(default) = settings.get("defaultPolicy") { rows.push(default.clone()); }
    if let Some(policies) = settings.get("policies").and_then(Value::as_array) {
        for policy in policies { if !rows.iter().any(|v| get_str(v, "id").ok() == get_str(policy, "id").ok()) { rows.push(policy.clone()); } }
    }
    rows
}

fn default_policy(settings: &Value) -> &Value { settings.get("defaultPolicy").unwrap_or(&Value::Null) }

pub(super) fn outbound_tag(interface_id: &str) -> String { format!("wan-{}", short_id(interface_id)) }
pub(super) fn selector_tag(policy_id: &str, family: &str) -> String { format!("policy-{}-{}", short_id(policy_id), if family == "IPv6" { "v6" } else { "v4" }) }
fn short_id(id: &str) -> String { id.bytes().take(8).map(|b| format!("{b:02x}")).collect() }

fn mapping<const N: usize>(values: [(&str, YamlValue); N]) -> YamlValue {
    let mut map = Mapping::new(); for (key, value) in values { map.insert(strv(key), value); } YamlValue::Mapping(map)
}
fn put(map: &mut Mapping, key: &str, value: impl Into<YamlValue>) { map.insert(strv(key), value.into()); }
fn strv(value: impl Into<String>) -> YamlValue { YamlValue::String(value.into()) }
fn list(values: impl IntoIterator<Item=YamlValue>) -> YamlValue { YamlValue::Sequence(values.into_iter().collect()) }

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
