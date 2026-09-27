import { open, save } from "@tauri-apps/plugin-dialog";
import {
  Activity,
  AlertCircle,
  ArrowDownToLine,
  ArrowDownUp,
  ArrowUpFromLine,
  Check,
  ChevronDown,
  CircleCheck,
  CircleDot,
  Download,
  FileArchive,
  FileText,
  HardDrive,
  LayoutDashboard,
  LoaderCircle,
  Moon,
  Network,
  Plus,
  RefreshCw,
  Route,
  Save,
  Search,
  Server,
  ShieldCheck,
  Sun,
  Trash2,
  Upload,
  Waypoints,
  Wifi,
  Wrench,
  X,
  Zap,
} from "lucide-react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { ReactNode } from "react";
import { call, saveExport } from "./ipc";
import type {
  AppSettings,
  ApplicationRule,
  ConnectionTelemetry,
  DiagnosticsBlob,
  InterfaceTestResult,
  LiveConnection,
  NetworkAdapter,
  OutboundTraffic,
  PolicyRouteStatus,
  RoutingPolicy,
  RuntimeDiagnostics,
  RuntimeLogEntry,
  RuntimeStatus,
} from "./types";

type Page =
  | "overview"
  | "monitor"
  | "interfaces"
  | "policies"
  | "rules"
  | "logs"
  | "diagnostics";

const navItems = [
  { id: "overview", label: "Overview", Icon: LayoutDashboard },
  { id: "monitor", label: "Live monitor", Icon: Activity },
  { id: "interfaces", label: "Interfaces", Icon: Network },
  { id: "policies", label: "Policies", Icon: Waypoints },
  { id: "rules", label: "App rules", Icon: Route },
  { id: "logs", label: "Logs", Icon: FileText },
  { id: "diagnostics", label: "Diagnostics", Icon: Wrench },
] as const;

const pageCopy: Record<Page, { title: string; subtitle: string }> = {
  overview: {
    title: "Network overview",
    subtitle: "Routing health and active policy paths at a glance.",
  },
  monitor: {
    title: "Live monitor",
    subtitle: "Traffic rates, interface totals, and active connections.",
  },
  interfaces: {
    title: "Network interfaces",
    subtitle: "Choose eligible exits and check IPv4 and IPv6 health.",
  },
  policies: {
    title: "Routing policies",
    subtitle: "Set primary and fallback exits, failover, and dual WAN.",
  },
  rules: {
    title: "Application rules",
    subtitle: "Route an executable or process through a selected policy.",
  },
  logs: {
    title: "Service logs",
    subtitle: "Recent service events and routing decisions.",
  },
  diagnostics: {
    title: "Diagnostics",
    subtitle: "Inspect runtime counters, validate, and export a support archive.",
  },
};

function messageOf(error: unknown): string {
  if (typeof error === "string") return error;
  if (error instanceof Error) return error.message;
  return "The request could not be completed.";
}

function formatBytes(bytes: number): string {
  let amount = Math.max(0, bytes);
  const units = ["B", "KB", "MB", "GB", "TB"];
  let index = 0;
  while (amount >= 1000 && index < units.length - 1) {
    amount /= 1000;
    index += 1;
  }
  return amount.toLocaleString(undefined, {
    maximumFractionDigits: amount >= 100 ? 0 : amount >= 10 ? 1 : 2,
  }) + " " + units[index];
}

function formatRate(bytes: number | null): string {
  return bytes === null ? "Sampling…" : formatBytes(bytes) + "/s";
}

function formatTime(value?: string | null): string {
  if (!value) return "—";
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleString();
}

function interfaceName(adapters: NetworkAdapter[], id?: string | null): string {
  if (!id) return "No fallback";
  return adapters.find((adapter) => adapter.id === id)?.name ?? "Unknown interface";
}

function blankPolicy(primaryId: string): RoutingPolicy {
  return {
    id: crypto.randomUUID(),
    name: "New policy",
    primaryInterfaceId: primaryId,
    fallbackInterfaceId: null,
    failoverEnabled: true,
    autoFailback: true,
    loadBalanceEnabled: false,
    primaryTrafficPercent: 50,
    enabled: true,
    failureThreshold: 3,
    recoveryThreshold: 3,
    recoveryStabilization: "00:00:10",
    minimumSwitchHoldTime: "00:00:15",
    orderedInterfaceCandidates: [],
  };
}

function blankRule(policyId: string): ApplicationRule {
  return {
    id: crypto.randomUUID(),
    displayName: "",
    executablePath: "",
    processName: "",
    enabled: true,
    policyId,
    priority: 0,
  };
}

function App() {
  const [page, setPage] = useState<Page>("overview");
  const [isDark, setIsDark] = useState(
    () => window.localStorage.getItem("easynetbalance-theme") === "dark",
  );
  const [busy, setBusy] = useState(false);
  const [loading, setLoading] = useState(true);
  const [actionName, setActionName] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState("");
  const [status, setStatus] = useState<RuntimeStatus | null>(null);
  const [adapters, setAdapters] = useState<NetworkAdapter[]>([]);
  const [settings, setSettings] = useState<AppSettings | null>(null);
  const [policies, setPolicies] = useState<RoutingPolicy[]>([]);
  const [rules, setRules] = useState<ApplicationRule[]>([]);
  const [logs, setLogs] = useState<RuntimeLogEntry[]>([]);
  const [diagnostics, setDiagnostics] = useState<RuntimeDiagnostics | null>(null);
  const [telemetry, setTelemetry] = useState<ConnectionTelemetry | null>(null);
  const [monitorError, setMonitorError] = useState("");
  const [selectedAdapterId, setSelectedAdapterId] = useState("");
  const [policyDraft, setPolicyDraft] = useState<RoutingPolicy | null>(null);
  const [ruleDraft, setRuleDraft] = useState<ApplicationRule | null>(null);
  const [logSearch, setLogSearch] = useState("");
  const [ratio, setRatio] = useState(50);
  const [rates, setRates] = useState(new Map<string, { up: number | null; down: number | null }>());
  const [redactIps, setRedactIps] = useState(true);
  const previousTelemetry = useRef<ConnectionTelemetry | null>(null);
  const ratioDirty = useRef(false);

  useEffect(() => {
    document.documentElement.dataset.theme = isDark ? "dark" : "light";
    window.localStorage.setItem("easynetbalance-theme", isDark ? "dark" : "light");
  }, [isDark]);

  useEffect(() => {
    if (page !== "monitor") {
      previousTelemetry.current = null;
      setRates(new Map());
    }
  }, [page]);

  const readAll = useCallback(async () => {
    const nextStatus = await call<RuntimeStatus>("GetStatus");
    setStatus(nextStatus);
    const nextAdapters = await call<NetworkAdapter[]>("GetAdapters");
    setAdapters(nextAdapters);
    setSelectedAdapterId((current) =>
      nextAdapters.some((item) => item.id === current)
        ? current
        : nextAdapters[0]?.id ?? "",
    );
    const nextSettings = await call<AppSettings>("GetSettings");
    setSettings(nextSettings);
    setPolicies(await call<RoutingPolicy[]>("GetPolicies"));
    setRules(await call<ApplicationRule[]>("GetRules"));
    setLogs(await call<RuntimeLogEntry[]>("GetLogs"));
    setDiagnostics(await call<RuntimeDiagnostics>("GetDiagnostics"));
    if (!ratioDirty.current) setRatio(nextSettings.defaultPolicy.primaryTrafficPercent ?? 50);
    setPolicyDraft((current) => {
      if (current) return current;
      const selected = [nextSettings.defaultPolicy, ...nextSettings.policies][0];
      return selected ? { ...selected } : null;
    });
    setRuleDraft((current) => {
      if (current) return current;
      return null;
    });
  }, []);

  const refreshAll = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      await readAll();
      setNotice("Service data refreshed.");
    } catch (cause) {
      setError(messageOf(cause));
    } finally {
      setLoading(false);
    }
  }, [readAll]);

  useEffect(() => {
    void refreshAll();
  }, [refreshAll]);

  const refreshStatus = useCallback(async () => {
    setStatus(await call<RuntimeStatus>("GetStatus"));
  }, []);

  const refreshMonitor = useCallback(async () => {
    try {
      const sample = await call<ConnectionTelemetry>("GetConnectionTelemetry");
      const previous = previousTelemetry.current;
      const seconds = previous
        ? (new Date(sample.sampledAt).getTime() - new Date(previous.sampledAt).getTime()) / 1000
        : 0;
      const previousById = new Map((previous?.outbounds ?? []).map((item) => [item.interfaceId, item]));
      setRates(
        new Map(
          sample.outbounds.map((item) => {
            const old = previousById.get(item.interfaceId);
            const valid =
              seconds > 0 &&
              seconds < 30 &&
              old !== undefined &&
              item.uploadBytes >= old.uploadBytes &&
              item.downloadBytes >= old.downloadBytes;
            return [
              item.interfaceId,
              {
                up: valid ? (item.uploadBytes - old!.uploadBytes) / seconds : null,
                down: valid ? (item.downloadBytes - old!.downloadBytes) / seconds : null,
              },
            ] as const;
          }),
        ),
      );
      setTelemetry(sample);
      setMonitorError(sample.lastError ?? "");
      previousTelemetry.current = sample;
    } catch (cause) {
      previousTelemetry.current = null;
      setRates(new Map());
      setMonitorError(messageOf(cause));
    }
  }, []);

  useEffect(() => {
    if (page !== "monitor") return;
    void refreshMonitor();
    const timer = window.setInterval(() => void refreshMonitor(), 2000);
    return () => window.clearInterval(timer);
  }, [page, refreshMonitor]);

  const loadBalanceSupported = status?.weightedBytesSupported === true;
  const balanced = Boolean(
    loadBalanceSupported &&
      settings?.defaultPolicy.loadBalanceEnabled &&
      settings.defaultPolicy.primaryInterfaceId &&
      settings.defaultPolicy.fallbackInterfaceId &&
      settings.defaultPolicy.primaryInterfaceId !== settings.defaultPolicy.fallbackInterfaceId,
  );

  useEffect(() => {
    if (!balanced || !ratioDirty.current) return;
    const value = ratio;
    const timer = window.setTimeout(() => {
      ratioDirty.current = false;
      void call<void>("SetTrafficRatio", { primaryTrafficPercent: value })
        .then(() => {
          setSettings((current) =>
            current
              ? {
                  ...current,
                  defaultPolicy: {
                    ...current.defaultPolicy,
                    primaryTrafficPercent: value,
                  },
                }
              : current,
          );
          setNotice("Traffic target saved: " + value + "% / " + (100 - value) + "%.");
          setError(null);
        })
        .catch((cause: unknown) => {
          setError("Ratio update failed: " + messageOf(cause));
          void call<AppSettings>("GetSettings")
            .then((current) => {
              setSettings(current);
              setRatio(current.defaultPolicy.primaryTrafficPercent ?? 50);
            })
            .catch(() => undefined);
        });
    }, 450);
    return () => window.clearTimeout(timer);
  }, [balanced, ratio]);

  const routingRows = status?.policies ?? [];
  const selectedAdapter = adapters.find((item) => item.id === selectedAdapterId);
  const policyOptions = useMemo(
    () => (settings ? [settings.defaultPolicy, ...settings.policies.filter((item) => item.id !== settings.defaultPolicy.id)] : policies),
    [settings, policies],
  );
  const allowedAdapters = useMemo(
    () => adapters.filter((item) => item.isUserAllowed),
    [adapters],
  );
  const visibleLogs = useMemo(() => {
    const query = logSearch.trim().toLocaleLowerCase();
    return [...logs]
      .sort((a, b) => Date.parse(b.timestamp) - Date.parse(a.timestamp))
      .filter((item) =>
        !query ||
        [item.level, item.source, item.message].some((value) =>
          value.toLocaleLowerCase().includes(query),
        ),
      );
  }, [logs, logSearch]);

  async function runAction(successText: string, work: () => Promise<void>) {
    setBusy(true);
    setActionName(successText);
    setNotice("");
    setError(null);
    try {
      await work();
      setNotice(successText);
    } catch (cause) {
      setError(messageOf(cause));
    } finally {
      setBusy(false);
      setActionName("");
    }
  }

  async function updateAdapter(allowed: boolean) {
    if (!selectedAdapter) throw new Error("Select an interface first.");
    await call<void>("SetInterfaceUsability", {
      interfaceId: selectedAdapter.id,
      allowed,
    });
    await readAll();
  }

  async function probeAdapter(family: "IPv4" | "IPv6") {
    if (!selectedAdapter) throw new Error("Select an interface first.");
    const result = await call<InterfaceTestResult>("TestInterface", {
      interfaceId: selectedAdapter.id,
      family,
    });
    setAdapters(await call<NetworkAdapter[]>("GetAdapters"));
    setNotice(
      selectedAdapter.name +
        " " +
        family +
        ": " +
        result.health +
        (result.latencyMs === null || result.latencyMs === undefined
          ? "."
          : ", " + Math.round(result.latencyMs) + " ms."),
    );
  }

  function pickPolicy(policy: RoutingPolicy) {
    setPolicyDraft({ ...policy });
  }

  function patchPolicy(patch: Partial<RoutingPolicy>) {
    setPolicyDraft((current) => (current ? { ...current, ...patch } : current));
  }

  async function savePolicy() {
    if (!policyDraft) throw new Error("Choose or create a policy first.");
    if (!policyDraft.name.trim()) throw new Error("Enter a policy name.");
    if (!policyDraft.primaryInterfaceId) throw new Error("Choose an allowed primary interface.");
    const policy = {
      ...policyDraft,
      name: policyDraft.name.trim(),
      fallbackInterfaceId: policyDraft.fallbackInterfaceId || null,
      loadBalanceEnabled:
        loadBalanceSupported &&
        policyDraft.id === settings?.defaultPolicy.id && policyDraft.loadBalanceEnabled,
    };
    if (
      policy.loadBalanceEnabled &&
      (!policy.fallbackInterfaceId ||
        policy.fallbackInterfaceId === policy.primaryInterfaceId)
    ) {
      throw new Error("Dual WAN needs two different allowed interfaces.");
    }
    await call<void>("SavePolicy", policy);
    await readAll();
    const saved = [settings?.defaultPolicy, ...policies].find((item) => item?.id === policy.id);
    setPolicyDraft({ ...(saved ?? policy), ...policy });
  }

  async function makeDefaultPolicy() {
    const policy = policyDraft ?? undefined;
    if (!policy) throw new Error("Select a policy first.");
    if (policy.id === settings?.defaultPolicy.id) throw new Error("This is already the default policy.");
    await call<void>("SetDefaultPolicy", { id: policy.id });
    await readAll();
    const next = await call<AppSettings>("GetSettings");
    setPolicyDraft({ ...next.defaultPolicy });
  }

  async function deletePolicy() {
    if (!policyDraft) throw new Error("Select a policy first.");
    if (policyDraft.id === settings?.defaultPolicy.id) throw new Error("The default policy cannot be deleted.");
    if (rules.some((rule) => rule.policyId === policyDraft.id)) {
      throw new Error("This policy is used by an application rule. Change the rule first.");
    }
    await call<void>("DeletePolicy", { id: policyDraft.id });
    setPolicyDraft(settings ? { ...settings.defaultPolicy } : null);
    await readAll();
  }

  function patchRule(patch: Partial<ApplicationRule>) {
    setRuleDraft((current) => (current ? { ...current, ...patch } : current));
  }

  async function browseExecutable() {
    const selected = await open({
      multiple: false,
      directory: false,
      filters: [{ name: "Executable files", extensions: ["exe"] }],
    });
    if (typeof selected !== "string") return;
    patchRule({
      executablePath: selected,
      displayName: ruleDraft?.displayName || selected.split(/[\\/]/).at(-1)?.replace(/\.exe$/i, "") || "",
    });
  }

  async function saveRule() {
    if (!ruleDraft) throw new Error("Choose or create a rule first.");
    if (!ruleDraft.executablePath?.trim() && !ruleDraft.processName?.trim()) {
      throw new Error("Enter an executable path or process name.");
    }
    if (!ruleDraft.policyId) throw new Error("Choose a policy.");
    const rule = {
      ...ruleDraft,
      displayName:
        ruleDraft.displayName.trim() ||
        ruleDraft.executablePath?.split(/[\\/]/).at(-1)?.replace(/\.exe$/i, "") ||
        ruleDraft.processName ||
        "Application",
      executablePath: ruleDraft.executablePath?.trim() || null,
      processName: ruleDraft.processName?.trim() || null,
    };
    await call<void>("SaveRule", rule);
    await readAll();
    setRuleDraft({ ...rule });
  }

  async function deleteRule() {
    if (!ruleDraft) throw new Error("Select a rule first.");
    await call<void>("DeleteRule", { id: ruleDraft.id });
    setRuleDraft(null);
    await readAll();
  }

  async function exportDiagnostics() {
    const result = await call<DiagnosticsBlob>("ExportDiagnostics", {
      redactIpAddresses: redactIps,
    });
    const path = await save({
      defaultPath: result.fileName,
      filters: [{ name: "ZIP archive", extensions: ["zip"] }],
    });
    if (!path) return;
    await saveExport(path, result.dataBase64);
  }

  return (
    <div className="app-frame">
      <aside className="sidebar">
        <div className="brand">
          <div className="brand-mark"><Route size={19} strokeWidth={2.4} /></div>
          <div>
            <div className="brand-name">EasyNetBalance</div>
            <div className="brand-caption">NETWORK CONTROL</div>
          </div>
        </div>

        <div className="nav-label">WORKSPACE</div>
        <nav className="navigation" aria-label="Main navigation">
          {navItems.map(({ id, label, Icon }) => (
            <button
              key={id}
              className={"nav-item " + (page === id ? "active" : "")}
              onClick={() => setPage(id)}
              aria-current={page === id ? "page" : undefined}
            >
              <Icon size={18} strokeWidth={1.8} />
              <span>{label}</span>
              {page === id && <span className="nav-current" />}
            </button>
          ))}
        </nav>

        <div className="sidebar-spacer" />
        <div className="service-card">
          <div className="service-card-head">
            <span className={"service-dot " + (status?.coreRunning ? "is-good" : "is-muted")} />
            <span>{status?.coreRunning ? "Service online" : status ? "Core stopped" : "Connecting…"}</span>
          </div>
          <div className="service-card-meta">
            {status?.routingEnabled ? "Routing enabled" : "Routing disabled"}
            {status?.version ? " · " + status.version : ""}
          </div>
        </div>
        <div className="sidebar-foot">
          <span>EasyNetBalance</span>
          <span>0.2.0</span>
        </div>
      </aside>

      <main className="main-area">
        <header className="topbar">
          <div className="topbar-context">
            <span className="eyebrow">CONTROL CENTER</span>
            <ChevronDown size={14} className="crumb-chevron" />
            <span className="crumb-current">{pageCopy[page].title}</span>
          </div>
          <div className="topbar-actions">
            <div className="service-top-status">
              <span className={"service-dot " + (status?.coreRunning ? "is-good" : "is-muted")} />
              <span>{status?.coreRunning ? "Running" : "Service unavailable"}</span>
            </div>
            <button
              className="icon-button"
              aria-label={isDark ? "Switch to light theme" : "Switch to dark theme"}
              title={isDark ? "Light theme" : "Dark theme"}
              onClick={() => setIsDark((value) => !value)}
            >
              {isDark ? <Sun size={17} /> : <Moon size={17} />}
            </button>
            <button className="button button-secondary refresh-button" disabled={loading || busy} onClick={() => void refreshAll()}>
              <RefreshCw size={15} className={loading ? "spin" : ""} />
              <span>Refresh</span>
            </button>
          </div>
        </header>

        <div className="workspace">
          {(error || notice) && (
            <div className={"notice-bar " + (error ? "notice-error" : "notice-success")} role="status">
              {error ? <AlertCircle size={17} /> : <CircleCheck size={17} />}
              <span>{error ?? notice}</span>
              {error && (
                <button className="notice-action" onClick={() => void refreshAll()}>
                  Retry
                </button>
              )}
              <button
                className="notice-close"
                aria-label="Dismiss message"
                onClick={() => {
                  setError(null);
                  setNotice("");
                }}
              >
                <X size={15} />
              </button>
            </div>
          )}

          <div className="page-heading">
            <div>
              <div className="page-kicker">EASYNETBALANCE / {page.toUpperCase()}</div>
              <h1>{pageCopy[page].title}</h1>
              <p>{pageCopy[page].subtitle}</p>
            </div>
            {busy && (
              <div className="working-indicator">
                <LoaderCircle size={15} className="spin" />
                <span>{actionName || "Working"}</span>
              </div>
            )}
          </div>

          {page === "overview" && (
            <OverviewPage
              status={status}
              adapters={adapters}
              routes={routingRows}
              loading={loading}
              busy={busy}
              onEnable={() =>
                void runAction("Routing enabled.", async () => {
                  await call<void>("EnableRouting");
                  await readAll();
                })
              }
              onDisable={() =>
                void runAction("Routing disabled.", async () => {
                  await call<void>("DisableRouting");
                  await readAll();
                })
              }
              nameFor={(id) => interfaceName(adapters, id)}
            />
          )}

          {page === "monitor" && (
            <MonitorPage
              telemetry={telemetry}
              rates={rates}
              monitorError={monitorError}
              settings={settings}
              adapters={adapters}
              ratio={ratio}
              balanced={balanced}
              balanceSupported={loadBalanceSupported}
              onRatioChange={(value) => {
                ratioDirty.current = true;
                setRatio(value);
              }}
              onGoPolicies={() => setPage("policies")}
            />
          )}

          {page === "interfaces" && (
            <InterfacesPage
              adapters={adapters}
              selectedId={selectedAdapterId}
              onSelect={setSelectedAdapterId}
              onAllow={() => void runAction("Interface allowed.", () => updateAdapter(true))}
              onExclude={() => void runAction("Interface excluded.", () => updateAdapter(false))}
              onProbe4={() => void runAction("IPv4 probe completed.", () => probeAdapter("IPv4"))}
              onProbe6={() => void runAction("IPv6 probe completed.", () => probeAdapter("IPv6"))}
              busy={busy}
            />
          )}

          {page === "policies" && (
            <PoliciesPage
              policies={policyOptions}
              settings={settings}
              adapters={allowedAdapters}
              draft={policyDraft}
              loadBalanceSupported={loadBalanceSupported}
              onSelect={pickPolicy}
              onNew={() => setPolicyDraft(blankPolicy(allowedAdapters[0]?.id ?? ""))}
              onPatch={patchPolicy}
              onSave={() =>
                void runAction("Policy saved.", async () => {
                  await savePolicy();
                  await refreshStatus();
                })
              }
              onMakeDefault={() =>
                void runAction("Default policy changed.", async () => {
                  await makeDefaultPolicy();
                  await refreshStatus();
                })
              }
              onDelete={() => void runAction("Policy deleted.", deletePolicy)}
              busy={busy}
            />
          )}

          {page === "rules" && (
            <RulesPage
              rules={rules}
              draft={ruleDraft}
              policies={policyOptions}
              onSelect={(rule) => setRuleDraft({ ...rule })}
              onNew={() => setRuleDraft(blankRule(settings?.defaultPolicy.id ?? ""))}
              onPatch={patchRule}
              onBrowse={() => void browseExecutable().catch((cause) => setError(messageOf(cause)))}
              onSave={() => void runAction("Application rule saved.", saveRule)}
              onDelete={() => void runAction("Application rule deleted.", deleteRule)}
              busy={busy}
            />
          )}

          {page === "logs" && (
            <LogsPage logs={visibleLogs} search={logSearch} onSearch={setLogSearch} onRefresh={() => void runAction("Logs refreshed.", async () => setLogs(await call<RuntimeLogEntry[]>("GetLogs")))} />
          )}

          {page === "diagnostics" && (
            <DiagnosticsPage
              diagnostics={diagnostics}
              redactIps={redactIps}
              onRedactChange={setRedactIps}
              onValidate={() => void runAction("Configuration is valid.", async () => call<void>("ValidateConfig"))}
              onRestart={() =>
                void runAction("Core restarted.", async () => {
                  await call<void>("RestartCore");
                  await readAll();
                })
              }
              onExport={() => void runAction("Diagnostics exported.", exportDiagnostics)}
              busy={busy}
            />
          )}
        </div>
      </main>
    </div>
  );
}

function OverviewPage(props: {
  status: RuntimeStatus | null;
  adapters: NetworkAdapter[];
  routes: PolicyRouteStatus[];
  loading: boolean;
  busy: boolean;
  onEnable: () => void;
  onDisable: () => void;
  nameFor: (id?: string | null) => string;
}) {
  const { status, adapters, routes, loading, busy } = props;
  const enabledPhysical = adapters.filter((item) => item.isUserAllowed && item.isPhysical).length;
  return (
    <div className="page-stack">
      <div className="overview-hero">
        <div className="hero-content">
          <div className="hero-kicker"><ShieldCheck size={15} /> ROUTING HEALTH</div>
          <div className="hero-row">
            <div>
              <h2>{status?.routingEnabled ? "Your traffic is protected" : "Routing is currently off"}</h2>
              <p>
                {status?.routingEnabled
                  ? "Network traffic is being managed by your active policies."
                  : "Enable routing to apply your policies to new network connections."}
              </p>
            </div>
            <div className="hero-actions">
              <span className={"status-pill " + (status?.routingEnabled ? "status-pill-green" : "status-pill-neutral")}>
                <span className="status-pill-dot" />
                {status?.routingEnabled ? "Active" : "Inactive"}
              </span>
              {status?.routingEnabled ? (
                <button className="button button-quiet" disabled={busy} onClick={props.onDisable}>Disable routing</button>
              ) : (
                <button className="button button-primary" disabled={busy || loading} onClick={props.onEnable}>
                  <Zap size={15} fill="currentColor" /> Enable routing
                </button>
              )}
            </div>
          </div>
          <div className="hero-stats">
            <div className="hero-stat">
              <span className="hero-stat-label">CORE</span>
              <span className="hero-stat-value">{status?.coreRunning ? "mihomo running" : status?.coreFaulted ? "Faulted" : "Stopped"}</span>
            </div>
            <div className="hero-stat">
              <span className="hero-stat-label">VERSION</span>
              <span className="hero-stat-value">{status?.version || "—"}</span>
            </div>
            <div className="hero-stat">
              <span className="hero-stat-label">UPTIME</span>
              <span className="hero-stat-value">{status?.uptime || "—"}</span>
            </div>
            <div className="hero-stat">
              <span className="hero-stat-label">LAST CHECK</span>
              <span className="hero-stat-value">{loading ? "Refreshing…" : "Just now"}</span>
            </div>
          </div>
        </div>
        <div className="hero-orbit orbit-one" />
        <div className="hero-orbit orbit-two" />
        <div className="hero-glow" />
      </div>

      {status?.lastError && (
        <div className="inline-warning">
          <AlertCircle size={17} />
          <div><strong>Service message</strong><span>{status.lastError}</span></div>
        </div>
      )}

      <div className="metric-grid">
        <MetricCard
          icon={<Server size={17} />}
          label="CORE STATUS"
          value={status?.coreRunning ? "Running" : status?.coreFaulted ? "Faulted" : "Stopped"}
          detail={status?.version ? "mihomo " + status.version : "Version unavailable"}
          accent={status?.coreRunning ? "green" : "amber"}
        />
        <MetricCard
          icon={<Network size={17} />}
          label="USABLE INTERFACES"
          value={String(enabledPhysical)}
          detail={adapters.length + " detected across the system"}
          accent="blue"
        />
        <MetricCard
          icon={<Waypoints size={17} />}
          label="ROUTING POLICIES"
          value={String(routes.length)}
          detail="Including the default policy"
          accent="violet"
        />
        <MetricCard
          icon={<Activity size={17} />}
          label="CORE UPTIME"
          value={status?.uptime || "—"}
          detail="Since the latest start"
          accent="blue"
        />
      </div>

      <section className="panel">
        <div className="section-heading">
          <div>
            <h3>Active policy routes</h3>
            <p>Current IPv4 and IPv6 egress chosen by the service.</p>
          </div>
          <span className="counter-badge">{routes.length} policies</span>
        </div>
        {routes.length === 0 ? (
          <EmptyState icon={<Waypoints size={20} />} title="No route data yet" detail="The service will show active exits after it starts." />
        ) : (
          <div className="table-wrap">
            <table>
              <thead><tr><th>POLICY</th><th>IPV4 EXIT</th><th>IPV6 EXIT</th><th>HEALTH</th></tr></thead>
              <tbody>
                {routes.map((route) => (
                  <tr key={route.policyId}>
                    <td><strong>{route.name}</strong>{route.policyId === routes[0]?.policyId && <span className="table-tag">DEFAULT</span>}</td>
                    <td>{route.noHealthyIPv4Interface ? <span className="text-danger">No healthy route</span> : props.nameFor(route.activeIPv4Interface)}</td>
                    <td>{route.noHealthyIPv6Interface ? <span className="text-danger">No healthy route</span> : props.nameFor(route.activeIPv6Interface)}</td>
                    <td><HealthPill healthy={!route.noHealthyIPv4Interface && !route.noHealthyIPv6Interface} label={route.noHealthyIPv4Interface || route.noHealthyIPv6Interface ? "Attention" : "Healthy"} /></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <div className="content-grid two-columns">
        <section className="panel panel-compact">
          <div className="section-heading">
            <div><h3>Interface readiness</h3><p>Physical links allowed for routing.</p></div>
            <Wifi size={18} className="heading-icon" />
          </div>
          <div className="compact-list">
            {adapters.filter((item) => item.isPhysical).slice(0, 5).map((adapter) => (
              <div className="compact-row" key={adapter.id}>
                <div className="compact-icon"><Network size={16} /></div>
                <div className="compact-primary"><strong>{adapter.name}</strong><span>{adapter.operationalStatus} · {adapter.isUserAllowed ? "Allowed" : "Excluded"}</span></div>
                <HealthPill healthy={adapter.isUserAllowed && adapter.operationalStatus === "Up"} label={adapter.isUserAllowed ? adapter.ipv4Health : "Excluded"} />
              </div>
            ))}
            {adapters.filter((item) => item.isPhysical).length === 0 && <div className="muted-empty">No physical interfaces were detected.</div>}
          </div>
        </section>

        <section className="panel panel-compact">
          <div className="section-heading">
            <div><h3>Service health</h3><p>Latest runtime status from EasyNetBalance.</p></div>
            <CircleDot size={18} className="heading-icon" />
          </div>
          <div className="health-summary">
            <div className="health-summary-mark"><ShieldCheck size={22} /></div>
            <div>
              <strong>{status?.coreRunning && status?.routingEnabled ? "Everything looks good" : "Service needs attention"}</strong>
              <span>{status?.lastError || (status?.coreRunning ? "The core is online and responding." : "Check the service status and installed runtime.")}</span>
            </div>
          </div>
          <div className="health-foot">
            <span><span className={"tiny-dot " + (status?.coreRunning ? "is-good" : "is-muted")} /> Service connection</span>
            <span>{status?.coreRunning ? "Connected" : "Not connected"}</span>
          </div>
        </section>
      </div>
    </div>
  );
}

function MetricCard(props: { icon: ReactNode; label: string; value: string; detail: string; accent: string }) {
  return (
    <div className="metric-card">
      <div className={"metric-icon accent-" + props.accent}>{props.icon}</div>
      <div className="metric-copy">
        <span className="metric-label">{props.label}</span>
        <strong>{props.value}</strong>
        <span className="metric-detail">{props.detail}</span>
      </div>
    </div>
  );
}

function MonitorPage(props: {
  telemetry: ConnectionTelemetry | null;
  rates: Map<string, { up: number | null; down: number | null }>;
  monitorError: string;
  settings: AppSettings | null;
  adapters: NetworkAdapter[];
  ratio: number;
  balanced: boolean;
  balanceSupported: boolean;
  onRatioChange: (value: number) => void;
  onGoPolicies: () => void;
}) {
  const { telemetry, rates } = props;
  const [connectionSearch, setConnectionSearch] = useState("");
  const connections = useMemo(() => {
    const query = connectionSearch.trim().toLocaleLowerCase();
    return (telemetry?.connections ?? []).filter((item) =>
      !query ||
      [item.process, item.destinationHost, item.destinationIp, item.actualOutbound, item.network]
        .some((value) => value.toLocaleLowerCase().includes(query)),
    );
  }, [telemetry, connectionSearch]);
  const primaryName = interfaceName(props.adapters, props.settings?.defaultPolicy.primaryInterfaceId);
  const fallbackName = interfaceName(props.adapters, props.settings?.defaultPolicy.fallbackInterfaceId);

  return (
    <div className="page-stack">
      {props.monitorError && (
        <div className="inline-warning"><AlertCircle size={17} /><div><strong>Monitor warning</strong><span>{props.monitorError}</span></div></div>
      )}
      <div className="telemetry-summary">
        <div className="telemetry-total">
          <span className="telemetry-title"><Activity size={16} /> LIVE TRAFFIC TOTAL</span>
          <strong>{formatBytes((telemetry?.uploadTotal ?? 0) + (telemetry?.downloadTotal ?? 0))}</strong>
          <span className="metric-detail">Session totals reported by the core</span>
        </div>
        <div className="telemetry-total">
          <span className="telemetry-title upload-label"><ArrowUpFromLine size={16} /> UPLOADED</span>
          <strong>{formatBytes(telemetry?.uploadTotal ?? 0)}</strong>
          <span className="metric-detail">Across active connections</span>
        </div>
        <div className="telemetry-total">
          <span className="telemetry-title download-label"><ArrowDownToLine size={16} /> DOWNLOADED</span>
          <strong>{formatBytes(telemetry?.downloadTotal ?? 0)}</strong>
          <span className="metric-detail">Across active connections</span>
        </div>
        <div className="telemetry-total">
          <span className="telemetry-title"><CircleDot size={16} /> ACTIVE CONNECTIONS</span>
          <strong>{telemetry?.connections.length ?? "—"}</strong>
          <span className="metric-detail">Last sample {telemetry ? new Date(telemetry.sampledAt).toLocaleTimeString() : "pending"}</span>
        </div>
      </div>

      <section className="panel">
        <div className="section-heading">
          <div><h3>Outbound interfaces</h3><p>Per interface speed, cumulative bytes, and the rolling byte window. Samples refresh every two seconds.</p></div>
          <span className="live-indicator"><span /> LIVE</span>
        </div>
        {!telemetry || telemetry.outbounds.length === 0 ? (
          <EmptyState icon={<Network size={20} />} title="Waiting for traffic samples" detail="Interface statistics appear when the service has active telemetry." />
        ) : (
          <div className="outbound-grid">
            {telemetry.outbounds.map((outbound) => (
              <OutboundCard key={outbound.interfaceId} item={outbound} rate={rates.get(outbound.interfaceId)} />
            ))}
          </div>
        )}
      </section>

      <section className="panel ratio-panel">
        <div className="section-heading">
          <div>
            <h3>Dual WAN traffic target</h3>
            <p>Set the target byte share for the default policy. Each connection keeps its selected exit.</p>
          </div>
          {props.balanced && <span className="counter-badge">DEFAULT POLICY</span>}
        </div>
        {props.balanced ? (
          <>
            <div className="ratio-summary">
              <div className="ratio-endpoint"><span className="ratio-index">01</span><div><strong>{primaryName}</strong><span>Primary interface</span></div></div>
              <div className="ratio-readout"><strong>{props.ratio}<span>%</span></strong><span>PRIMARY SHARE</span></div>
              <div className="ratio-endpoint ratio-endpoint-right"><span className="ratio-index">02</span><div><strong>{fallbackName}</strong><span>Fallback interface</span></div></div>
              <div className="ratio-readout ratio-readout-secondary"><strong>{100 - props.ratio}<span>%</span></strong><span>FALLBACK SHARE</span></div>
            </div>
            <div className="slider-row">
              <span>0%</span>
              <input aria-label="Primary interface traffic share" type="range" min="1" max="99" step="1" value={props.ratio} onChange={(event) => props.onRatioChange(Number(event.target.value))} />
              <span>100%</span>
            </div>
            <p className="form-hint">The target is measured over the rolling byte window. New TCP and UDP flows keep affinity to their selected exit.</p>
          </>
        ) : !props.balanceSupported ? (
          <div className="configure-callout">
            <div className="configure-icon"><ArrowDownUp size={18} /></div>
            <div><strong>Byte-metered dual WAN is unavailable</strong><span>The service has not advertised weighted-bytes support, so no ratio is being applied.</span></div>
          </div>
        ) : (
          <div className="configure-callout">
            <div className="configure-icon"><ArrowDownUp size={18} /></div>
            <div><strong>Enable dual WAN to tune the traffic split</strong><span>Choose two different allowed interfaces in the default policy editor.</span></div>
            <button className="button button-secondary" onClick={props.onGoPolicies}>Open policies</button>
          </div>
        )}
      </section>

      <section className="panel">
        <div className="section-heading section-heading-wrap">
          <div><h3>Active connections</h3><p>Detected process, destination, and actual or predicted exit.</p></div>
          <div className="table-tools">
            <div className="search-box"><Search size={15} /><input value={connectionSearch} onChange={(event) => setConnectionSearch(event.target.value)} placeholder="Filter connections" /></div>
            <span className="counter-badge">{connections.length} active</span>
          </div>
        </div>
        <div className="table-wrap table-wrap-tall">
          <table className="wide-table">
            <thead><tr><th>PROCESS</th><th>DESTINATION</th><th>NETWORK</th><th>ACTUAL EXIT</th><th>EXPECTED EXIT</th><th>UPLOADED</th><th>DOWNLOADED</th></tr></thead>
            <tbody>
              {connections.map((item) => <ConnectionRow key={item.id || item.process + item.destinationIp} item={item} />)}
            </tbody>
          </table>
          {connections.length === 0 && <EmptyState icon={<Activity size={20} />} title="No matching connections" detail={connectionSearch ? "Try another filter." : "Active connections will appear after traffic is routed."} />}
        </div>
      </section>
    </div>
  );
}

function OutboundCard(props: { item: OutboundTraffic; rate?: { up: number | null; down: number | null } }) {
  const { item, rate } = props;
  return (
    <article className={"outbound-card " + (!item.available ? "outbound-unavailable" : "")}>
      <div className="outbound-card-top">
        <div className="outbound-symbol"><Network size={17} /></div>
        <div className="outbound-title"><strong>{item.name}</strong><span>{item.available ? "Available" : item.error || "Unavailable"}</span></div>
        {item.targetPercent !== null && item.targetPercent !== undefined && <span className="target-tag">{item.targetPercent}% target</span>}
      </div>
      <div className="speed-grid">
        <div className="speed-cell">
          <span><ArrowUpFromLine size={13} /> UPLOAD</span>
          <strong>{formatRate(rate?.up ?? null)}</strong>
        </div>
        <div className="speed-cell">
          <span><ArrowDownToLine size={13} /> DOWNLOAD</span>
          <strong>{formatRate(rate?.down ?? null)}</strong>
        </div>
      </div>
      <div className="outbound-window">
        <div><span>ROLLING WINDOW</span><strong>↑ {formatBytes(item.rollingUploadBytes ?? 0)} · ↓ {formatBytes(item.rollingDownloadBytes ?? 0)}</strong></div>
        <div><span>ACTUAL SHARE</span><strong>{item.actualShare === null || item.actualShare === undefined ? "—" : item.actualShare.toFixed(1) + "%"}</strong></div>
        <div><span>HEALTH</span><strong>{item.healthy === null || item.healthy === undefined ? (item.available ? "Available" : "Unavailable") : item.healthy ? "Healthy" : "Unhealthy"}</strong></div>
        {item.warmingUp && <div><span>RECOVERY WARMUP</span><strong>{item.warmupProgress === null || item.warmupProgress === undefined ? "Active" : Math.round(item.warmupProgress * 100) + "%"}</strong></div>}
      </div>
      <div className="outbound-foot">
        <span>↑ {formatBytes(item.uploadBytes)}</span>
        <span>↓ {formatBytes(item.downloadBytes)}</span>
        <span>{item.activeConnections} connections</span>
      </div>
    </article>
  );
}

function ConnectionRow({ item }: { item: LiveConnection }) {
  const destination = item.destinationHost
    ? item.destinationHost + " (" + item.destinationIp + ":" + item.destinationPort + ")"
    : item.destinationIp + ":" + item.destinationPort;
  return (
    <tr>
      <td><div className="process-cell"><span className="process-icon"><CircleDot size={14} /></span><div><strong>{item.process || "Unknown"}</strong><span title={item.processPath}>{item.processPath || "Process details unavailable"}</span></div></div></td>
      <td className="destination-cell" title={destination}>{destination}</td>
      <td><span className="protocol-tag">{item.network || "—"}</span></td>
      <td><span className="exit-label">{item.actualOutbound || "Unknown"}</span></td>
      <td><span className="exit-label muted-exit">{item.predictedOutbound || "Unknown"}</span></td>
      <td className="number-cell">{formatBytes(item.uploadBytes)}</td>
      <td className="number-cell">{formatBytes(item.downloadBytes)}</td>
    </tr>
  );
}

function InterfacesPage(props: {
  adapters: NetworkAdapter[];
  selectedId: string;
  onSelect: (id: string) => void;
  onAllow: () => void;
  onExclude: () => void;
  onProbe4: () => void;
  onProbe6: () => void;
  busy: boolean;
}) {
  const [search, setSearch] = useState("");
  const list = useMemo(() => {
    const query = search.trim().toLocaleLowerCase();
    return props.adapters.filter((item) =>
      !query || [item.name, item.description, item.networkInterfaceType].some((value) => value.toLocaleLowerCase().includes(query)),
    );
  }, [props.adapters, search]);
  const selected = props.adapters.find((item) => item.id === props.selectedId);
  return (
    <div className="page-stack">
      <div className="metric-grid three-metrics">
        <MetricCard icon={<Network size={17} />} label="DETECTED" value={String(props.adapters.length)} detail="Interfaces found by Windows" accent="blue" />
        <MetricCard icon={<ShieldCheck size={17} />} label="ALLOWED" value={String(props.adapters.filter((item) => item.isUserAllowed).length)} detail="Eligible routing exits" accent="green" />
        <MetricCard icon={<Wifi size={17} />} label="PHYSICAL LINKS" value={String(props.adapters.filter((item) => item.isPhysical).length)} detail="Physical network devices" accent="violet" />
      </div>

      <section className="panel">
        <div className="section-heading section-heading-wrap">
          <div><h3>Available interfaces</h3><p>Allow an interface to use it in policies, or exclude it from routing.</p></div>
          <div className="table-tools"><div className="search-box"><Search size={15} /><input value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Find interface" /></div><span className="counter-badge">{list.length} found</span></div>
        </div>
        <div className="table-wrap">
          <table className="interface-table">
            <thead><tr><th>INTERFACE</th><th>LINK</th><th>TYPE</th><th>IPV4</th><th>IPV6</th><th>IPV4 ADDRESS</th><th>GATEWAY</th><th>ROUTING</th></tr></thead>
            <tbody>
              {list.map((item) => (
                <tr key={item.id} className={props.selectedId === item.id ? "selected-row" : ""} onClick={() => props.onSelect(item.id)}>
                  <td><div className="interface-name"><span className="interface-symbol"><Network size={15} /></span><div><strong>{item.name}</strong><span title={item.description}>{item.description || item.id}</span></div></div></td>
                  <td><span className={"link-status " + (item.operationalStatus === "Up" ? "link-up" : "link-down")}><span />{item.operationalStatus}</span></td>
                  <td>{item.networkInterfaceType}{item.isPhysical ? <span className="table-tag table-tag-soft">PHYSICAL</span> : null}</td>
                  <td><HealthPill healthy={item.ipv4Health === "Healthy"} label={item.ipv4Health} /></td>
                  <td><HealthPill healthy={item.ipv6Health === "Healthy"} label={item.ipv6Health} /></td>
                  <td className="small-cell" title={item.ipv4Addresses.join(", ")}>{item.ipv4Addresses[0] || "—"}</td>
                  <td className="small-cell" title={item.ipv4Gateways.join(", ")}>{item.ipv4Gateways[0] || "—"}</td>
                  <td><span className={"allowed-status " + (item.isUserAllowed ? "allowed-yes" : "allowed-no")}><span>{item.isUserAllowed ? <Check size={11} /> : <X size={11} />}</span>{item.isUserAllowed ? "Allowed" : "Excluded"}</span></td>
                </tr>
              ))}
            </tbody>
          </table>
          {list.length === 0 && <EmptyState icon={<Network size={20} />} title="No interfaces found" detail="Try changing your search filter." />}
        </div>
      </section>

      <section className="panel interface-detail-panel">
        {selected ? (
          <>
            <div className="section-heading">
              <div><h3>{selected.name}</h3><p>{selected.description || "Interface details and probe controls"}</p></div>
              <span className={"status-pill " + (selected.isUserAllowed ? "status-pill-green" : "status-pill-neutral")}><span className="status-pill-dot" />{selected.isUserAllowed ? "Allowed for routing" : "Excluded from routing"}</span>
            </div>
            <div className="details-grid">
              <DetailItem label="INTERFACE ID" value={selected.id} mono />
              <DetailItem label="LINK SPEED" value={selected.speed > 0 ? formatBytes(selected.speed / 8) + "/s" : "Unknown"} />
              <DetailItem label="IPV4 ADDRESSES" value={selected.ipv4Addresses.join(", ") || "None"} />
              <DetailItem label="IPV4 GATEWAYS" value={selected.ipv4Gateways.join(", ") || "None"} />
              <DetailItem label="IPV6 ADDRESSES" value={selected.ipv6Addresses.join(", ") || "None"} />
              <DetailItem label="DNS SERVERS" value={selected.dnsServers.join(", ") || "None"} />
              <DetailItem label="LAST PROBE" value={formatTime(selected.lastProbeTime)} />
              <DetailItem label="ADAPTER CLASS" value={selected.isPhysical ? "Physical" : selected.isVirtual ? "Virtual" : "Other"} />
            </div>
            <div className="panel-actions">
              {selected.isUserAllowed ? (
                <button className="button button-secondary" disabled={props.busy} onClick={props.onExclude}><X size={15} /> Exclude interface</button>
              ) : (
                <button className="button button-primary" disabled={props.busy} onClick={props.onAllow}><Check size={15} /> Allow interface</button>
              )}
              <button className="button button-secondary" disabled={props.busy} onClick={props.onProbe4}><Activity size={15} /> Probe IPv4</button>
              <button className="button button-secondary" disabled={props.busy} onClick={props.onProbe6}><Activity size={15} /> Probe IPv6</button>
            </div>
          </>
        ) : <EmptyState icon={<Network size={20} />} title="Select an interface" detail="Choose a row above to inspect its addresses or run health probes." />}
      </section>
    </div>
  );
}

function DetailItem(props: { label: string; value: string; mono?: boolean }) {
  return <div className="detail-item"><span>{props.label}</span><strong className={props.mono ? "mono" : ""} title={props.value}>{props.value}</strong></div>;
}

function PoliciesPage(props: {
  policies: RoutingPolicy[];
  settings: AppSettings | null;
  adapters: NetworkAdapter[];
  draft: RoutingPolicy | null;
  loadBalanceSupported: boolean;
  onSelect: (policy: RoutingPolicy) => void;
  onNew: () => void;
  onPatch: (patch: Partial<RoutingPolicy>) => void;
  onSave: () => void;
  onMakeDefault: () => void;
  onDelete: () => void;
  busy: boolean;
}) {
  const isDefault = props.draft?.id === props.settings?.defaultPolicy.id;
  return (
    <div className="page-stack">
      <div className="policy-layout">
        <section className="panel policy-list-panel">
          <div className="section-heading">
            <div><h3>Policies</h3><p>Pick a policy to edit its routes.</p></div>
            <button className="icon-button icon-button-small" title="Create a new policy" onClick={props.onNew}><Plus size={17} /></button>
          </div>
          <div className="policy-list">
            {props.policies.map((policy) => {
              const selected = policy.id === props.draft?.id;
              const primary = interfaceName(props.adapters, policy.primaryInterfaceId);
              return (
                <button key={policy.id} className={"policy-list-item " + (selected ? "selected" : "")} onClick={() => props.onSelect(policy)}>
                  <span className="policy-item-icon"><Waypoints size={17} /></span>
                  <span className="policy-item-copy"><strong>{policy.name}</strong><span>{primary}{policy.fallbackInterfaceId ? " + " + interfaceName(props.adapters, policy.fallbackInterfaceId) : ""}</span></span>
                  {policy.id === props.settings?.defaultPolicy.id ? <span className="default-tag">DEFAULT</span> : <span className="policy-chevron">›</span>}
                </button>
              );
            })}
            {props.policies.length === 0 && <div className="empty-list">No policies available.</div>}
          </div>
          <div className="policy-list-actions">
            <button className="button button-secondary button-full" disabled={!props.draft || isDefault || props.busy} onClick={props.onMakeDefault}>Make default</button>
            <button className="button button-danger-quiet button-full" disabled={!props.draft || isDefault || props.busy} onClick={props.onDelete}><Trash2 size={14} /> Delete selected</button>
          </div>
        </section>

        <section className="panel editor-panel">
          <div className="section-heading">
            <div><h3>{props.draft ? "Policy editor" : "Choose a policy"}</h3><p>{isDefault ? "This policy handles traffic without a matching application rule." : "Configure the interface choices and failover behavior."}</p></div>
            {isDefault && <span className="default-tag">DEFAULT POLICY</span>}
          </div>
          {props.draft ? (
            <div className="form-layout">
              <label className="field field-span"><span>POLICY NAME</span><input value={props.draft.name} onChange={(event) => props.onPatch({ name: event.target.value })} placeholder="Policy name" /></label>
              <label className="field"><span>PRIMARY INTERFACE</span><select value={props.draft.primaryInterfaceId} onChange={(event) => props.onPatch({ primaryInterfaceId: event.target.value })}><option value="">Choose an interface</option>{props.adapters.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>
              <label className="field"><span>FALLBACK INTERFACE</span><select value={props.draft.fallbackInterfaceId || ""} onChange={(event) => props.onPatch({ fallbackInterfaceId: event.target.value || null })}><option value="">No fallback</option>{props.adapters.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>

              <div className="form-divider field-span"><span>BEHAVIOR</span></div>
              <div className="toggle-grid field-span">
                <ToggleRow title="Policy enabled" detail="Allow matching traffic to use this policy." checked={props.draft.enabled} onChange={(value) => props.onPatch({ enabled: value })} />
                <ToggleRow title="Automatic failover" detail="Use the fallback when the primary link is unhealthy." checked={props.draft.failoverEnabled} onChange={(value) => props.onPatch({ failoverEnabled: value })} />
                <ToggleRow title="Automatic failback" detail="Return to the primary after it recovers." checked={props.draft.autoFailback} onChange={(value) => props.onPatch({ autoFailback: value })} />
                {isDefault && (
                  <ToggleRow title="Dual WAN load balance" detail={props.loadBalanceSupported ? "Assign new flows by actual bytes over the rolling window; each flow stays on its selected exit." : "Unavailable until byte-metered forwarding is implemented in the service."} checked={props.draft.loadBalanceEnabled && props.loadBalanceSupported} disabled={!props.loadBalanceSupported} onChange={(value) => props.onPatch({ loadBalanceEnabled: value })} />
                )}
              </div>
              {isDefault && (
                <div className="form-note field-span"><Activity size={15} /><span>{props.loadBalanceSupported ? "Set the live primary share from the Live monitor page. The fallback receives the remaining share." : "This saved setting is not applied by the current service. The editor and monitor keep the ratio disabled."}</span></div>
              )}
              <div className="editor-footer field-span">
                <span className="form-hint">{props.draft.id ? "Changes take effect after saving." : ""}</span>
                <button className="button button-primary" disabled={props.busy} onClick={props.onSave}><Save size={15} /> Save policy</button>
              </div>
            </div>
          ) : (
            <EmptyState icon={<Waypoints size={20} />} title="No policy selected" detail="Choose a policy from the list or create one." />
          )}
        </section>
      </div>
    </div>
  );
}

function ToggleRow(props: { title: string; detail: string; checked: boolean; disabled?: boolean; onChange: (value: boolean) => void }) {
  return (
    <label className="toggle-row">
      <span><strong>{props.title}</strong><small>{props.detail}</small></span>
      <input type="checkbox" checked={props.checked} disabled={props.disabled} onChange={(event) => props.onChange(event.target.checked)} />
      <span className="toggle-control" />
    </label>
  );
}

function RulesPage(props: {
  rules: ApplicationRule[];
  draft: ApplicationRule | null;
  policies: RoutingPolicy[];
  onSelect: (rule: ApplicationRule) => void;
  onNew: () => void;
  onPatch: (patch: Partial<ApplicationRule>) => void;
  onBrowse: () => void;
  onSave: () => void;
  onDelete: () => void;
  busy: boolean;
}) {
  const orderedRules = [...props.rules].sort((a, b) => b.priority - a.priority || a.displayName.localeCompare(b.displayName));
  return (
    <div className="page-stack">
      <section className="panel">
        <div className="section-heading section-heading-wrap">
          <div><h3>Application rules</h3><p>Executable path takes precedence; process name is used when no path is provided.</p></div>
          <button className="button button-secondary" onClick={props.onNew}><Plus size={15} /> New rule</button>
        </div>
        <div className="table-wrap">
          <table className="rules-table">
            <thead><tr><th>APPLICATION</th><th>EXECUTABLE / PROCESS</th><th>POLICY</th><th>STATUS</th><th /></tr></thead>
            <tbody>
              {orderedRules.map((rule) => (
                <tr key={rule.id} className={props.draft?.id === rule.id ? "selected-row" : ""} onClick={() => props.onSelect(rule)}>
                  <td><div className="process-cell"><span className="process-icon"><Route size={14} /></span><div><strong>{rule.displayName || rule.processName || "Unnamed rule"}</strong><span>{rule.enabled ? "Rule is active" : "Rule is disabled"}</span></div></div></td>
                  <td className="small-cell" title={rule.executablePath || rule.processName || ""}>{rule.executablePath || rule.processName || "—"}</td>
                  <td>{props.policies.find((item) => item.id === rule.policyId)?.name ?? "Unknown policy"}</td>
                  <td><span className={"link-status " + (rule.enabled ? "link-up" : "link-down")}><span />{rule.enabled ? "Enabled" : "Disabled"}</span></td>
                  <td><button className="text-button" onClick={(event) => { event.stopPropagation(); props.onSelect(rule); }}>Edit</button></td>
                </tr>
              ))}
            </tbody>
          </table>
          {orderedRules.length === 0 && <EmptyState icon={<Route size={20} />} title="No application rules" detail="Add a rule to send one application through a specific policy." />}
        </div>
      </section>

      <section className="panel">
        <div className="section-heading">
          <div><h3>{props.draft ? "Rule editor" : "Create or edit a rule"}</h3><p>Choose an executable path or process name and assign a policy.</p></div>
          {props.draft && <span className="counter-badge">RULE ID · {props.draft.id.slice(0, 8)}</span>}
        </div>
        {props.draft ? (
          <div className="form-layout rule-form">
            <label className="field"><span>DISPLAY NAME</span><input value={props.draft.displayName} onChange={(event) => props.onPatch({ displayName: event.target.value })} placeholder="Example: Browser" /></label>
            <label className="field"><span>PROCESS NAME</span><input value={props.draft.processName ?? ""} onChange={(event) => props.onPatch({ processName: event.target.value })} placeholder="browser.exe" /></label>
            <label className="field field-span"><span>EXECUTABLE PATH <em>OPTIONAL, PREFERRED FOR EXACT MATCHING</em></span><div className="field-inline"><input value={props.draft.executablePath ?? ""} onChange={(event) => props.onPatch({ executablePath: event.target.value })} placeholder="C:\Program Files\App\app.exe" /><button className="button button-secondary" onClick={props.onBrowse}><Upload size={15} /> Browse</button></div></label>
            <label className="field"><span>POLICY</span><select value={props.draft.policyId} onChange={(event) => props.onPatch({ policyId: event.target.value })}><option value="">Choose a policy</option>{props.policies.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>
            <div className="field"><span>STATUS</span><label className="checkbox-row"><input type="checkbox" checked={props.draft.enabled} onChange={(event) => props.onPatch({ enabled: event.target.checked })} /><span>Enable this rule</span></label></div>
            <div className="editor-footer field-span">
              <button className="button button-danger-quiet" disabled={props.busy || !props.rules.some((item) => item.id === props.draft?.id)} onClick={props.onDelete}><Trash2 size={15} /> Delete rule</button>
              <button className="button button-primary" disabled={props.busy} onClick={props.onSave}><Save size={15} /> Save rule</button>
            </div>
          </div>
        ) : <EmptyState icon={<FileText size={20} />} title="Select or create a rule" detail="The editor will show the selected rule's matching details." />}
      </section>
    </div>
  );
}

function LogsPage(props: { logs: RuntimeLogEntry[]; search: string; onSearch: (value: string) => void; onRefresh: () => void }) {
  return (
    <div className="page-stack">
      <section className="panel log-panel">
        <div className="section-heading section-heading-wrap">
          <div><h3>Recent service events</h3><p>Newest entries appear first. Search by level, source, or message.</p></div>
          <div className="table-tools">
            <div className="search-box"><Search size={15} /><input value={props.search} onChange={(event) => props.onSearch(event.target.value)} placeholder="Search logs" /></div>
            <button className="button button-secondary" onClick={props.onRefresh}><RefreshCw size={15} /> Refresh logs</button>
          </div>
        </div>
        <div className="table-wrap log-table-wrap">
          <table className="logs-table">
            <thead><tr><th>TIME</th><th>LEVEL</th><th>SOURCE</th><th>MESSAGE</th></tr></thead>
            <tbody>
              {props.logs.map((log, index) => (
                <tr key={log.timestamp + log.source + index}>
                  <td className="time-cell">{formatTime(log.timestamp)}</td>
                  <td><LogLevel level={log.level} /></td>
                  <td><span className="source-label">{log.source}</span></td>
                  <td className="log-message">{log.message}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {props.logs.length === 0 && <EmptyState icon={<FileText size={20} />} title="No log entries" detail={props.search ? "No entries match this search." : "The service has not produced any log events yet."} />}
        </div>
        <div className="panel-footnote">{props.logs.length} entries shown</div>
      </section>
    </div>
  );
}

function LogLevel({ level }: { level: string }) {
  const normalized = level.toLocaleLowerCase();
  const style = normalized.includes("error") || normalized.includes("critical")
    ? "log-level-error"
    : normalized.includes("warn")
      ? "log-level-warn"
      : normalized.includes("debug")
        ? "log-level-debug"
        : "log-level-info";
  return <span className={"log-level " + style}>{level}</span>;
}

function DiagnosticsPage(props: {
  diagnostics: RuntimeDiagnostics | null;
  redactIps: boolean;
  onRedactChange: (value: boolean) => void;
  onValidate: () => void;
  onRestart: () => void;
  onExport: () => void;
  busy: boolean;
}) {
  const item = props.diagnostics;
  const coreMemory = item?.mihomoWorkingSetBytes ?? item?.singBoxWorkingSetBytes;
  return (
    <div className="page-stack">
      <section className="panel">
        <div className="section-heading">
          <div><h3>Runtime counters</h3><p>Live health, routing, memory, and generated configuration counters.</p></div>
          <span className={"status-pill " + (item?.coreRunning ? "status-pill-green" : "status-pill-neutral")}><span className="status-pill-dot" />{item?.coreRunning ? "Core running" : "Core stopped"}</span>
        </div>
        <div className="diagnostic-grid">
          <DiagnosticTile icon={<Activity size={16} />} label="HEALTH PROBES" value={String(item?.healthProbes ?? "—")} detail={(item?.successfulProbes ?? 0) + " passed · " + (item?.failedProbes ?? 0) + " failed"} />
          <DiagnosticTile icon={<Waypoints size={16} />} label="ROUTE CHANGES" value={String((item?.failoverCount ?? 0) + (item?.failbackCount ?? 0))} detail={(item?.failoverCount ?? 0) + " failovers · " + (item?.failbackCount ?? 0) + " failbacks"} />
          <DiagnosticTile icon={<Route size={16} />} label="POLICIES & RULES" value={String(item?.policiesCount ?? "—") + " / " + String(item?.rulesCount ?? "—")} detail="Policies · application rules" />
          <DiagnosticTile icon={<Zap size={16} />} label="GENERATED ROUTES" value={String(item?.generatedRouteRules ?? "—")} detail={(item?.generatedSelectors ?? 0) + " selectors · " + (item?.generatedDirectOutbounds ?? 0) + " interface exits"} />
        </div>
        <div className="diagnostic-memory">
          <div className="memory-title"><HardDrive size={16} /><strong>Working set</strong></div>
          <div className="memory-row"><span>Service</span><strong>{item ? formatBytes(item.serviceWorkingSetBytes) : "—"}</strong></div>
          <div className="memory-row"><span>mihomo core</span><strong>{coreMemory ? formatBytes(coreMemory) : "—"}</strong></div>
          <div className="memory-row"><span>EasyNetBalance UI</span><strong>{item?.uiWorkingSetBytes ? formatBytes(item.uiWorkingSetBytes) : "—"}</strong></div>
          <div className="memory-row"><span>Health contexts</span><strong>{item?.healthContexts ?? "—"}</strong></div>
        </div>
      </section>

      <section className="panel">
        <div className="section-heading"><div><h3>Maintenance actions</h3><p>Validate the active configuration or restart the core process.</p></div><Wrench size={18} className="heading-icon" /></div>
        <div className="maintenance-actions">
          <div className="maintenance-card">
            <div className="maintenance-icon"><ShieldCheck size={19} /></div>
            <div><strong>Validate configuration</strong><span>Ask the service to validate the configuration it generated.</span></div>
            <button className="button button-secondary" disabled={props.busy} onClick={props.onValidate}>Validate</button>
          </div>
          <div className="maintenance-card">
            <div className="maintenance-icon"><RefreshCw size={19} /></div>
            <div><strong>Restart core</strong><span>Restart mihomo while keeping service settings in place.</span></div>
            <button className="button button-secondary" disabled={props.busy} onClick={props.onRestart}>Restart</button>
          </div>
        </div>
      </section>

      <section className="panel">
        <div className="section-heading"><div><h3>Export diagnostics</h3><p>Save a ZIP archive containing runtime data and service logs.</p></div><FileArchive size={18} className="heading-icon" /></div>
        <div className="export-row">
          <div className="export-copy"><span className="export-icon"><Download size={18} /></span><div><strong>Diagnostic archive</strong><span>Review the contents before sharing the archive.</span></div></div>
          <label className="checkbox-row redact-choice"><input type="checkbox" checked={props.redactIps} onChange={(event) => props.onRedactChange(event.target.checked)} /><span>Redact IP addresses</span></label>
          <button className="button button-primary" disabled={props.busy} onClick={props.onExport}><Download size={15} /> Export ZIP</button>
        </div>
      </section>
    </div>
  );
}

function DiagnosticTile(props: { icon: ReactNode; label: string; value: string; detail: string }) {
  return <div className="diagnostic-tile"><span className="diagnostic-icon">{props.icon}</span><div><span className="metric-label">{props.label}</span><strong>{props.value}</strong><small>{props.detail}</small></div></div>;
}

function HealthPill(props: { healthy: boolean; label: string }) {
  const healthy = props.healthy;
  const normalized = props.label.toLocaleLowerCase();
  const className = healthy ? "health-good" : normalized === "unknown" || normalized === "unavailable" || normalized === "excluded" ? "health-neutral" : "health-warn";
  return <span className={"health-pill " + className}><span />{props.label}</span>;
}

function EmptyState(props: { icon: ReactNode; title: string; detail: string }) {
  return <div className="empty-state"><span className="empty-icon">{props.icon}</span><strong>{props.title}</strong><span>{props.detail}</span></div>;
}

export default App;
