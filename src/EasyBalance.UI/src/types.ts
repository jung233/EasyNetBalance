export type Health =
  | "Unknown"
  | "Healthy"
  | "Suspect"
  | "Down"
  | "Recovering"
  | "Unavailable";

export interface PolicyRouteStatus {
  policyId: string;
  name: string;
  activeIPv4Interface?: string | null;
  activeIPv6Interface?: string | null;
  noHealthyIPv4Interface: boolean;
  noHealthyIPv6Interface: boolean;
}

export interface RuntimeStatus {
  routingEnabled: boolean;
  coreRunning: boolean;
  coreFaulted: boolean;
  version?: string | null;
  uptime?: string | null;
  lastError?: string | null;
  weightedBytesSupported?: boolean;
  policies: PolicyRouteStatus[];
}

export interface NetworkAdapter {
  id: string;
  name: string;
  description: string;
  networkInterfaceType: string;
  operationalStatus: string;
  speed: number;
  ipv4Addresses: string[];
  ipv6Addresses: string[];
  ipv4Gateways: string[];
  ipv6Gateways: string[];
  dnsServers: string[];
  isPhysical: boolean;
  isVirtual: boolean;
  isUserAllowed: boolean;
  ipv4Health: Health;
  ipv6Health: Health;
  ipv4Latency?: string | null;
  ipv6Latency?: string | null;
  lastProbeTime?: string | null;
}

export interface RoutingPolicy {
  id: string;
  name: string;
  primaryInterfaceId: string;
  fallbackInterfaceId?: string | null;
  failoverEnabled: boolean;
  autoFailback: boolean;
  loadBalanceEnabled: boolean;
  primaryTrafficPercent: number;
  enabled: boolean;
  failureThreshold: number;
  recoveryThreshold: number;
  recoveryStabilization: string;
  minimumSwitchHoldTime: string;
  orderedInterfaceCandidates: string[];
}

export interface ApplicationRule {
  id: string;
  displayName: string;
  executablePath?: string | null;
  processName?: string | null;
  enabled: boolean;
  policyId: string;
  priority: number;
  createdAt?: string;
  updatedAt?: string;
}

export interface AppSettings {
  enabled: boolean;
  strictRoute: boolean;
  ipv6Enabled: boolean;
  disconnectOldConnectionsOnFailover: boolean;
  showVirtualInterfaces: boolean;
  healthyProbeInterval: string;
  suspectProbeInterval: string;
  downProbeInterval: string;
  probeTimeout: string;
  defaultPolicy: RoutingPolicy;
  policies: RoutingPolicy[];
  applicationRules: ApplicationRule[];
  interfaceUsabilityOverrides: Record<string, boolean>;
}

export interface RuntimeLogEntry {
  timestamp: string;
  level: string;
  source: string;
  message: string;
}

export interface RuntimeDiagnostics {
  healthProbes: number;
  successfulProbes: number;
  failedProbes: number;
  failoverCount: number;
  failbackCount: number;
  rulesCount: number;
  policiesCount: number;
  healthContexts: number;
  servicePrivateBytes: number;
  serviceWorkingSetBytes: number;
  serviceCpuSeconds: number;
  coreRunning: boolean;
  routingEnabled: boolean;
  mihomoPrivateBytes?: number | null;
  mihomoWorkingSetBytes?: number | null;
  mihomoCpuSeconds?: number | null;
  singBoxPrivateBytes?: number | null;
  singBoxWorkingSetBytes?: number | null;
  singBoxCpuSeconds?: number | null;
  singBoxVersion?: string | null;
  uiPrivateBytes?: number | null;
  uiWorkingSetBytes?: number | null;
  uiCpuSeconds?: number | null;
  generatedRouteRules: number;
  generatedSelectors: number;
  generatedDirectOutbounds: number;
}

export interface OutboundTraffic {
  interfaceId: string;
  name: string;
  uploadBytes: number;
  downloadBytes: number;
  activeConnections: number;
  targetPercent?: number | null;
  rollingBytes?: number | null;
  rollingUploadBytes?: number;
  rollingDownloadBytes?: number;
  actualShare?: number | null;
  targetShare?: number | null;
  effectiveWeight?: number | null;
  healthy?: boolean | null;
  warmingUp?: boolean;
  warmupProgress?: number | null;
  available: boolean;
  error?: string | null;
}

export interface LiveConnection {
  id: string;
  process: string;
  processPath: string;
  destinationIp: string;
  destinationHost: string;
  destinationPort: string;
  network: string;
  actualOutbound: string;
  predictedOutbound: string;
  uploadBytes: number;
  downloadBytes: number;
  startedAt?: string | null;
}

export interface ConnectionTelemetry {
  sampledAt: string;
  lastError?: string | null;
  uploadTotal: number;
  downloadTotal: number;
  outbounds: OutboundTraffic[];
  connections: LiveConnection[];
}

export interface DiagnosticsBlob {
  fileName: string;
  dataBase64: string;
}

export interface InterfaceTestResult {
  interfaceId: string;
  family: string;
  health: string;
  latencyMs?: number | null;
}
