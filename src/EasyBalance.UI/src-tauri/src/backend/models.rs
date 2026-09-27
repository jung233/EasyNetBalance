use serde_json::{json, Value};

pub(super) fn default_settings() -> Value {
    json!({
        "enabled": false,
        "strictRoute": true,
        "ipv6Enabled": true,
        "disconnectOldConnectionsOnFailover": false,
        "showVirtualInterfaces": false,
        "healthyProbeInterval": "00:00:20",
        "suspectProbeInterval": "00:00:02",
        "downProbeInterval": "00:00:08",
        "probeTimeout": "00:00:03",
        "defaultPolicy": {
            "id": "default",
            "name": "Default",
            "primaryInterfaceId": "",
            "fallbackInterfaceId": null,
            "failoverEnabled": true,
            "autoFailback": true,
            "loadBalanceEnabled": false,
            "primaryTrafficPercent": 50,
            "enabled": true,
            "failureThreshold": 3,
            "recoveryThreshold": 3,
            "recoveryStabilization": "00:00:10",
            "minimumSwitchHoldTime": "00:00:15",
            "orderedInterfaceCandidates": []
        },
        "policies": [],
        "applicationRules": [],
        "probeEndpoints": [
            {"id":"probe-msft","name":"Microsoft connectivity","url":"https://www.msftconnecttest.com/connecttest.txt","enabled":true,"requireIPv4":true,"requireIPv6":true},
            {"id":"probe-cloudflare","name":"Cloudflare connectivity","url":"https://www.cloudflare.com/cdn-cgi/trace","enabled":true,"requireIPv4":true,"requireIPv6":true},
            {"id":"probe-google","name":"Google connectivity","url":"https://www.gstatic.com/generate_204","enabled":true,"requireIPv4":true,"requireIPv6":true}
        ],
        "interfaceUsabilityOverrides": {}
    })
}

pub(super) fn merge_defaults(target: &mut Value, defaults: Value) {
    match (target, defaults) {
        (Value::Object(target), Value::Object(defaults)) => {
            for (key, default_value) in defaults {
                if let Some(value) = target.get_mut(&key) { merge_defaults(value, default_value); }
                else { target.insert(key, default_value); }
            }
        }
        (Value::Null, defaults) => *target = defaults,
        _ => {}
    }
}

pub(super) fn get_str(value: &Value, key: &str) -> Result<String, String> {
    let found = lookup(value, key).ok_or_else(|| format!("Missing required value: {key}"))?;
    found.as_str().map(str::to_owned).ok_or_else(|| format!("Invalid value for {key}"))
}

pub(super) fn get_i64(value: &Value, key: &str) -> Result<i64, String> {
    let found = lookup(value, key).ok_or_else(|| format!("Missing required value: {key}"))?;
    found.as_i64().ok_or_else(|| format!("Invalid number for {key}"))
}

pub(super) fn get_bool(value: &Value, key: &str, default: bool) -> bool {
    lookup(value, key).and_then(Value::as_bool).unwrap_or(default)
}

fn lookup<'a>(value: &'a Value, key: &str) -> Option<&'a Value> {
    value.as_object()?.iter().find(|(name, _)| name.eq_ignore_ascii_case(key)).map(|(_, value)| value)
}
