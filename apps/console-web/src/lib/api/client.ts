export type ServerCapabilities = {
  serverId: string;
  officialRestConnected: boolean;
  publishAnnouncements: boolean;
  publishChatAnnouncements: boolean;
  publishClientOverlay: boolean;
  publishTopBanner: boolean;
  commandQueueReady: boolean;
  auditReady: boolean;
  bridgeConnected: boolean;
  readPlayers: boolean;
  readPlayerProgression?: boolean;
  writePlayerProgression?: boolean;
  readInventory: boolean;
  writeInventory: boolean;
  readPals: boolean;
  writePals: boolean;
  mode: string;
  reasons: string[];
};

export type ServerConfigurationOption = {
  key: string;
  kind: string;
  value: unknown;
  defaultValue: unknown;
  sensitive: boolean;
  hasValue: boolean;
  allowedValues: string[] | null;
  minimum: number | null;
  maximum: number | null;
  step: number | null;
  customized: boolean;
};

export type ServerConfiguration = {
  schemaVersion: number;
  revision: string;
  options: ServerConfigurationOption[];
  filePath: string;
  defaultFilePath: string;
  lastModifiedAt: string;
};

export type ServerConfigurationUpdate = {
  revision: string;
  changes: Record<string, unknown>;
};

export async function getServerConfiguration(signal?: AbortSignal): Promise<ServerConfiguration> {
  const response = await fetch("/api/v1/servers/local/configuration", { signal });
  if (!response.ok) throw new Error(await getApiErrorMessage(response, "读取服务器配置失败"));
  return response.json() as Promise<ServerConfiguration>;
}

export async function updateServerConfiguration(input: ServerConfigurationUpdate): Promise<ServerConfiguration> {
  const response = await fetch("/api/v1/servers/local/configuration", {
    method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(input)
  });
  if (!response.ok) {
    const body = await response.json().catch(() => null) as {
      message?: string;
      detail?: string;
      errors?: Record<string, string[]>;
    } | null;
    throw new Error(body?.errors?.configuration?.[0] ?? body?.message ?? body?.detail ?? `保存服务器配置失败（HTTP ${response.status}）`);
  }
  return response.json() as Promise<ServerConfiguration>;
}

export type AnnouncementChannel = "chat" | "top-banner" | "client-overlay";

export type AnnouncementInput = {
  title: string;
  body: string;
  audience: {
    type: "global";
    ids: string[] | null;
  };
  channels: AnnouncementChannel[];
  publishAt: string | null;
  expiresAt: string | null;
};

export type Announcement = AnnouncementInput & {
  announcementId: string;
  state: "draft" | "scheduled" | "published" | "expired" | "cancelled";
  createdAt: string;
  updatedAt: string;
};

export type CommandState =
  | "accepted"
  | "dispatched"
  | "succeeded"
  | "failed"
  | "uncertain"
  | "cancelled";

export type ChannelDeliveryResult = {
  channel: AnnouncementChannel;
  state: string;
  httpStatus?: number | null;
  attemptedRecipients?: number | null;
  deliveredRecipients?: number | null;
  error?: {
    code: string;
    message: string;
  } | null;
};

export type CommandStatus = {
  commandId: string;
  state: CommandState;
  createdAt: string;
  completedAt: string | null;
  result: {
    announcementId: string;
    channels: ChannelDeliveryResult[];
  } | null;
  error: {
    code: string;
    message: string;
  } | null;
  statusUrl: string;
};

export type NativePlayerObject = {
  ephemeralObjectId: number;
  objectName: string;
  fullName: string;
  className: string;
  identity: {
    playerUId: string | null;
    accountName: string | null;
    displayName: string | null;
    playerId: number | null;
    level: number | null;
    levelSource: "official-rest";
  };
};

export type NativePlayerProbe = {
  observedAt: string;
  executionThread: string;
  targetClass: string;
  classFound: boolean;
  identityMapping: {
    ready: boolean;
    playerUId: string;
    playerUIdType: string;
    accountName: string;
    displayName: string;
    playerId: string;
    levelSource: "official-rest";
  };
  objectCount: number;
  truncated: boolean;
  objects: NativePlayerObject[];
};

export type NativePlayerField = {
  name: string;
  type: string;
  detailType: string | null;
  owner: string;
  replicated: boolean;
};

export type NativePlayerSchema = {
  observedAt: string;
  executionThread: string;
  targetClass: string;
  classFound: boolean;
  propertyCount: number;
  truncated: boolean;
  properties: NativePlayerField[];
  identityCandidates: NativePlayerField[];
  candidateFunctions: string[];
  inheritance: string[];
};

export type PlayerStatusPoint = {
  id: string;
  nativeName?: string;
  rank: number;
};

export type PlayerProgression = {
  playerUId: string;
  instanceId: string | null;
  displayName: string | null;
  accountName: string | null;
  playerId: number | null;
  online: boolean;
  loaded: boolean;
  level: number;
  totalExperience: number;
  experienceToNextLevel: number | null;
  unusedStatusPoints: number;
  statusPoints: PlayerStatusPoint[];
  technologyPoints: number | null;
  ancientTechnologyPoints: number | null;
  revision: string;
};

export type PlayerProgressionPatch = {
  addExperience?: number | null;
  targetLevel?: number | null;
  grantStatusPoints?: number | null;
  grantTechnologyPoints?: number | null;
  grantAncientTechnologyPoints?: number | null;
  allocateStatusId?: string | null;
  allocateStatusPoints?: number | null;
};

export type PlayerProgressionMutationResult = {
  dryRun: boolean;
  applied: boolean;
  operation: string;
  nativeFunction: string;
  value?: number;
  readBackVerified?: boolean;
  revision?: string;
  before?: Record<string, number>;
  preview?: Record<string, number>;
  after?: Record<string, number>;
};

export type NativeInventoryType = {
  name: string;
  fullName: string;
  kind: "class" | "struct" | "other";
  propertyCount: number;
  truncated: boolean;
  properties: NativePlayerField[];
  candidateFunctions: string[];
};

export type NativeInventorySchema = {
  observedAt: string;
  executionThread: string;
  typeCount: number;
  missingTypes: string[];
  types: NativeInventoryType[];
};

export type NativeInventorySlot = {
  slotIndex: number;
  staticItemId: string;
  stackCount: number;
};

export type NativeInventoryContainer = {
  kind: string;
  containerId: string | null;
  resolved: boolean;
  slotCount: number;
  truncated: boolean;
  slots: Array<NativeInventorySlot | null>;
};

export type NativeInventoryProbe = {
  observedAt: string;
  executionThread: string;
  mappingReady: boolean;
  inventoryObjectCount: number;
  containerObjectCount: number;
  truncated: boolean;
  inventories: Array<{
    ownerPlayerUId: string | null;
    objectName: string;
    containers: NativeInventoryContainer[];
  }>;
};

export type InventoryMutationResult = {
  dryRun: boolean;
  applied: boolean;
  settlement: {
    functions: string[];
    planned: boolean;
    aggregateVerified: boolean;
  };
  slot: {
    containerId: string;
    containerKind: string;
    slotIndex: number;
    itemId: string;
    quantity: number;
    requestedQuantity?: number;
    previousQuantity?: number;
  };
};

export type NativePal = {
  instanceId: string;
  ownerPlayerUId: string;
  characterId: string;
  nickname: string;
  level: number;
  rank: number;
  exp: number;
  rare: boolean;
  favorite: boolean;
  talents: {
    hp: number;
    melee: number;
    shot: number;
    defense: number;
  };
  passiveSkills: string[];
  activeSkills: {
    equipped: Array<{ id: string; value: number }>;
    mastered: Array<{ id: string; value: number }>;
  };
  location: {
    containerId: string | null;
    slotIndex: number;
  };
  revision: string;
};

export type NativePalProbe = {
  observedAt: string;
  executionThread: string;
  mappingReady: boolean;
  parameterObjectCount: number;
  palCount: number;
  truncated: boolean;
  pals: NativePal[];
};

export type PalMutationInput = {
  nickname: string | null;
  favorite: boolean | null;
  passiveSkill?: {
    index: number;
    expectedSkillId: string;
    skillId: string;
  } | null;
  equippedActiveSkills?: string[] | null;
  reason: string;
  dryRun: boolean;
};

export type PalSkillEffect = {
  type: string;
  value: number;
  target: string;
};

export type PassiveSkillCatalogEntry = {
  id: string;
  name: string;
  description: string;
  rank: number;
  category: string;
  polarity: "positive" | "negative" | "neutral";
  localized: boolean;
  obtainable: boolean;
  internal: boolean;
  effects: PalSkillEffect[];
};

export type PalSkillCatalog = {
  observedAt: string;
  executionThread: string;
  locale: string;
  catalogRevision: string;
  activeEnum: string;
  activeSkillCount: number;
  activeSkills: Array<{
    id: string;
    value: number;
    name: string;
    description: string;
    localized: boolean;
  }>;
  passiveSkillCount: number;
  localizedPassiveSkillCount: number;
  obtainablePassiveSkillCount: number;
  passiveSkills: PassiveSkillCatalogEntry[];
  passiveSources: string[];
};

export type PalMutationResult = {
  dryRun: boolean;
  applied: boolean;
  settlement: {
    function: string;
    planned: boolean;
    mirrorSynchronized: boolean;
    readBackVerified: boolean;
  };
  pal: NativePal;
};

export type PlayerSummary = {
  playerId: string;
  uid: string | null;
  name: string;
  online: boolean;
  level: number | null;
};

export type LiveMapStatus = "live" | "stale" | "unavailable";

export type LiveMapBounds = {
  minX: number;
  maxX: number;
  minY: number;
  maxY: number;
};

export type LiveMapCoordinateSpace = {
  mapId: string;
  units: string;
  bounds: LiveMapBounds;
  projection: {
    axisSwap: boolean;
    invertX: boolean;
    invertY?: boolean;
  };
  backgroundUrl?: string | null;
};

export type LiveMapPosition = {
  x: number;
  y: number;
};

export type LiveMapPlayer = {
  playerId: string;
  uid: string | null;
  name: string;
  level: number | null;
  online: boolean;
  observedAt: string | null;
  position: LiveMapPosition;
};

export type LiveMapSnapshot = {
  serverId: string;
  streamId: string;
  sequence: number;
  status: LiveMapStatus;
  source: string;
  observedAt: string | null;
  generatedAt: string;
  sampleIntervalMs: number;
  staleAfterMs: number;
  unavailableAfterMs: number;
  coordinateSpace: LiveMapCoordinateSpace;
  items: LiveMapPlayer[];
};

export type SaveStatus = {
  serverId: string;
  ready: boolean;
  checkedAt: string;
  worldGuid: string | null;
  worldName: string | null;
  gameVersion: string | null;
  onlinePlayerCount: number | null;
  save: {
    fileCount: number;
    playerFileCount: number;
    totalBytes: number;
    lastModifiedAt: string | null;
  };
  disk: {
    availableBytes: number;
    totalBytes: number;
  };
  nativeBackups: {
    count: number;
    totalBytes: number;
    latestCreatedAt: string | null;
  };
  managedBackups: {
    count: number;
    totalBytes: number;
    verifiedCount: number;
    latestCreatedAt: string | null;
  };
  validation: {
    processPathMatched: boolean;
    serverNameMatched: boolean;
    worldGuidMatched: boolean;
  };
  error: ApiProblem | null;
};

export type BackupKind = "managed" | "native";

export type SaveBackup = {
  backupId: string;
  kind: BackupKind;
  label: string | null;
  worldGuid: string | null;
  gameVersion: string | null;
  createdAt: string;
  fileCount: number;
  totalBytes: number;
  integrity: string;
  consistency: string;
  actor: string | null;
  reason: string | null;
  manifestSha256: string | null;
};

export type SaveCommand = {
  commandId: string;
  type: string;
  state: string;
  stage: string;
  createdAt: string;
  completedAt: string | null;
  statusUrl: string;
  backupId: string | null;
  result: Record<string, unknown> | null;
  error: ApiProblem | null;
};

export type ApiProblem = {
  code: string;
  message: string;
};

export type PalDefenderLocation = {
  x: number;
  y: number;
  z: number;
};

export type PalDefenderCatalogEntry = {
  method: "GET" | "POST";
  path: string;
  permission: string;
  description: string;
};

export type PalDefenderCatalog = {
  basePath: string;
  count: number;
  items: PalDefenderCatalogEntry[];
};

export type PalDefenderVersion = {
  Version: {
    Major: number;
    Minor: number;
    Patch: number;
    Build: number;
    Version: string;
    VersionLong: string;
    Beta: boolean;
  };
};

export type PalDefenderStatus = {
  enabled: boolean;
  connected: boolean;
  baseUrl: string;
  version: PalDefenderVersion | null;
  upstreamStatus?: number | null;
  error: ApiProblem | null;
};

export type PalDefenderPlayerRecord = {
  Name: string;
  IP: string;
  PlayerUID: string;
  UserId: string;
  GuildName: string;
  GuildUUID: string;
  Status: string;
  WorldLocation: PalDefenderLocation;
  MapLocation: PalDefenderLocation;
};

export type PalDefenderPlayers = {
  Meta: {
    PlayerCount: number;
    OnlineCount: number;
  };
  Players: PalDefenderPlayerRecord[];
};

export type PalDefenderPlayer = {
  Player: PalDefenderPlayerRecord;
};

export type PalDefenderProgression = {
  Meta: {
    PlayerUID: string;
    Player: string;
  };
  Progression: {
    Player: {
      level: number;
      exp: number;
      unusedStatusPoints: number;
    };
    Currencies: {
      relics: Record<string, number>;
      technologyPoints: number;
      ancientTechnologyPoints: number;
    };
    Bosses: {
      towerBossDefeatCounts: Record<string, number>;
      normalBossDefeatFlags: Record<string, boolean>;
      raidBossDefeatCounts: Record<string, number>;
      totalBossDefeatCount: number;
      predatorDefeatCount: number;
    };
    Captures: {
      tribeCaptureCount: number;
      palCaptureCounts: Record<string, number>;
      palCaptureBonusCounts: Record<string, number>;
      palButcherCounts: Record<string, number>;
    };
    Activities: {
      craftItemCounts: Record<string, number>;
      normalDungeonClearCount: number;
      fixedDungeonClearCount: number;
      oilrigClearCount: number;
      palRankUpCounts: Record<string, number>;
      arenaSoloClearCounts: Record<string, number>;
      npcTalkCounts: Record<string, number>;
      fishingCounts: Record<string, number>;
      foundTreasureCount: number;
      campConqueredCount: number;
      firstFishingComplete: boolean;
    };
  };
};

export type PalDefenderItemSlot = {
  ItemID: string;
  Count: number;
};

export type PalDefenderInventoryContainer = {
  Available: boolean;
  ContainerID: string;
  UsedSlots: number;
  MaxSlots: number;
  FreeSlots: number;
  Slots: Record<string, PalDefenderItemSlot>;
};

export type PalDefenderItems = {
  Meta: {
    PlayerUID: string;
    Player: string;
  };
  Inventory: {
    Items: PalDefenderInventoryContainer;
    KeyItems: PalDefenderInventoryContainer;
    Weapons: PalDefenderInventoryContainer;
    Armor: PalDefenderInventoryContainer;
    Food: PalDefenderInventoryContainer;
    DropSlot: PalDefenderInventoryContainer;
  };
};

export type PalDefenderPalRecord = {
  PalID: string;
  UniqueNPCID: string;
  Nickname: string;
  SkinId: string;
  Gender: string;
  Level: number;
  Exp: number;
  Shiny: boolean;
  PartnerSkillLevel: number;
  CondensedPals: number;
  UnusedStatusPoints: number;
  FriendshipPoints: number;
  PhysicalHealth: string;
  WorkerSick: string;
  ImportedCharacter: boolean;
  HP: number;
  MP?: number;
  SP?: number;
  Shield?: number;
  Hunger: number;
  MaxHunger: number;
  SAN: number;
  Support: number;
  CraftSpeed: number;
  PalSouls: {
    Health: number;
    Attack: number;
    Defense: number;
    CraftSpeed: number;
  };
  IVs: {
    Health: number;
    AttackMelee: number;
    AttackShot: number;
    Defense: number;
  };
  ActiveSkills: string[];
  LearntSkills: string[];
  Passives: string[];
  ExtraWorkSuitabilities: Record<string, number>;
  DisableWorkPreferences: string[];
  team_slot_index?: number;
  page?: number;
  slot?: number;
  base_camp_slot_index?: number;
};

export type PalDefenderBaseCamp = {
  id: string;
  level: number;
  world_pos: PalDefenderLocation;
  map_pos: PalDefenderLocation;
  state: string;
  pals: Record<string, PalDefenderPalRecord>;
};

export type PalDefenderPals = {
  Meta: {
    PlayerUID: string;
    Player: string;
    TeamCount: number;
    PalboxCount: number;
    BaseCampCount: number;
  };
  Pals: {
    Team: Record<string, PalDefenderPalRecord>;
    Palbox: Record<string, PalDefenderPalRecord>;
    BaseCamps: PalDefenderBaseCamp[];
  };
};

export type PalDefenderTechs = {
  Meta: {
    PlayerUID: string;
    Player: string;
    UnlockedCount: number;
    LockedCount: number;
    TotalCount: number;
  };
  Techs: {
    Unlocked: string[];
  };
};

export type PalDefenderGuildSummary = {
  name: string;
  Level: number;
  admin: {
    id: string;
    name: string;
  };
  camp_count: number;
  camps: Array<{
    id: string;
    world_pos: PalDefenderLocation;
    map_pos: PalDefenderLocation;
  }>;
  member_count: number;
  members: string[];
};

export type PalDefenderGuilds = {
  Meta: {
    GuildCount: number;
  };
  Guilds: Record<string, PalDefenderGuildSummary>;
};

export type PalDefenderGuildPal = {
  nickname: string;
  pal_id: string;
  npc_id: string;
  skin_id: string;
  gender: string;
  level: number;
  shiny: boolean;
  phisical_health: string;
  worker_sick: string;
  san: number;
  imported: boolean;
  friendship: number;
  active_skills: string[];
  learnt_skills: string[];
  passives: string[];
};

export type PalDefenderGuild = {
  Guild: {
    name: string;
    Level: number;
    admin: {
      id: string;
      name: string;
    };
    member_count: number;
    members: Array<{
      player_uid: string;
      player_name: string;
      status: string;
    }>;
    camp_count: number;
    camps: Array<{
      id: string;
      level: number;
      world_pos: PalDefenderLocation;
      map_pos: PalDefenderLocation;
      state: string;
      pals: Record<string, PalDefenderGuildPal>;
      buildings: unknown;
    }>;
    items: Record<string, unknown> & {
      container_id?: string;
      current: number;
      max: number;
    };
    expeditions: {
      finished: number;
      missions: Record<string, boolean>;
    };
    laboratory: {
      current_research: string;
      researches: Record<string, {
        work_amount: number;
        required_work_amount: number;
        percentage: number;
      }>;
    };
  };
};

export type PalDefenderBanIssuer = {
  Type: string;
  NameValue: string;
  IP: string;
  Reason: string;
  Timestamp: {
    UTC: number;
    Year: number;
    Month: number;
    Day: number;
    Hour: number;
    Min: number;
    Sec: number;
    Msec: number;
  };
};

export type PalDefenderBanlist = {
  Banlist: {
    Version: number;
    BannedMessage: string;
    UserEntries: Array<{
      UserId: string;
      Active: boolean;
      BannedBy: PalDefenderBanIssuer;
      UnbannedBy?: PalDefenderBanIssuer | null;
    }>;
    IPEntries: Array<{
      IP: string;
      Active: boolean;
      BannedBy: PalDefenderBanIssuer;
      UnbannedBy?: PalDefenderBanIssuer | null;
    }>;
  };
};

export type PalDefenderBanlistQuery = {
  active?: boolean;
  entryType?: string;
  userId?: string;
  ip?: string;
  userIP?: string;
  issuerType?: string;
  issuerName?: string;
  issuerIP?: string;
  reason?: string;
  q?: string;
};

export type GameCatalogEntry = {
  id: string;
  name: string;
  englishName?: string | null;
  category: string;
  dex?: string | null;
};

export type PalTemplateCatalogEntry = {
  fileName: string;
  palId: string | null;
  nickname: string | null;
  level: number | null;
  shiny: boolean | null;
  passiveCount: number;
  activeSkillCount: number;
  sizeBytes: number;
  lastModifiedAt: string;
  sha256: string;
  parseable: boolean;
  selectable: boolean;
  riskLevel: "standard" | "elevated" | "high" | "invalid";
  summary: string;
};

export type GameResourceCatalog = {
  schemaVersion: string;
  revision: string;
  generatedAt: string;
  source: {
    name: string;
    note: string;
    itemsUrl: string;
    palsUrl: string;
    technologiesUrl: string;
    palNamesUrl?: string | null;
    palNamesLicense?: string | null;
  };
  coverage: {
    items: string;
    pals: string;
    eggs: string;
    technologies: string;
    templates: string;
  };
  items: GameCatalogEntry[];
  pals: GameCatalogEntry[];
  eggs: GameCatalogEntry[];
  technologies: GameCatalogEntry[];
  templates: PalTemplateCatalogEntry[];
};

export type PalDefenderCommandState =
  | "accepted"
  | "dispatched"
  | "succeeded"
  | "failed"
  | "uncertain";

export type PalDefenderCommand = {
  commandId: string;
  state: PalDefenderCommandState;
  createdAt: string;
  completedAt: string | null;
  result: {
    upstreamPath: string;
    httpStatus: number;
    body: unknown | null;
    text: string | null;
  } | null;
  error: ApiProblem | null;
  statusUrl: string;
};

export type PalDefenderAuditEvent = {
  eventId: string;
  commandId: string;
  eventType: PalDefenderCommandState | "recovered-uncertain";
  state: PalDefenderCommandState;
  at: string;
  serverId: string;
  upstreamPath: string;
  idempotencyKey: string;
  requestHash: string;
  reason: string;
  actor: string;
  httpStatus: number | null;
  errorCode: string | null;
  errorMessage: string | null;
};

export type SubmitPalDefenderCommandInput = {
  path: string;
  payload: unknown;
  reason: string;
  idempotencyKey: string;
  serverId?: string;
};

const fallbackCapabilities: ServerCapabilities = {
  serverId: "local",
  officialRestConnected: false,
  publishAnnouncements: false,
  publishChatAnnouncements: false,
  publishClientOverlay: false,
  publishTopBanner: false,
  commandQueueReady: false,
  auditReady: false,
  bridgeConnected: false,
  readPlayers: false,
  readInventory: false,
  writeInventory: false,
  readPals: false,
  writePals: false,
  mode: "api-offline",
  reasons: ["CONTROL_API_UNREACHABLE"]
};

export async function createAnnouncement(
  input: AnnouncementInput,
  idempotencyKey: string
): Promise<Announcement> {
  const response = await fetch("/api/v1/servers/local/announcements", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "Idempotency-Key": idempotencyKey
    },
    body: JSON.stringify(input)
  });
  if (!response.ok) {
    throw new Error(await getApiErrorMessage(response, "公告草稿创建失败"));
  }
  return (await response.json()) as Announcement;
}

export async function publishAnnouncement(
  announcementId: string,
  idempotencyKey: string
): Promise<CommandStatus> {
  const response = await fetch(
    `/api/v1/servers/local/announcements/${encodeURIComponent(announcementId)}/publish`,
    {
      method: "POST",
      headers: {
        "Idempotency-Key": idempotencyKey
      }
    }
  );
  if (!response.ok) {
    throw new Error(await getApiErrorMessage(response, "公告发布命令提交失败"));
  }
  return (await response.json()) as CommandStatus;
}

export async function getCommandStatus(
  statusUrl: string,
  signal?: AbortSignal
): Promise<CommandStatus> {
  const response = await fetch(statusUrl, { signal });
  if (!response.ok) {
    throw new Error(await getApiErrorMessage(response, "命令状态读取失败"));
  }
  return (await response.json()) as CommandStatus;
}

async function getApiErrorMessage(response: Response, fallback: string): Promise<string> {
  const error = await response.json().catch(() => null) as {
    code?: string;
    message?: string;
    detail?: string;
    error?: {
      code?: string;
      message?: string;
    };
    Error?: {
      Code?: string;
      Message?: string;
    };
  } | null;
  return error?.message
    ?? error?.detail
    ?? error?.error?.message
    ?? error?.Error?.Message
    ?? `${fallback}（HTTP ${response.status}）`;
}

export async function getCapabilities(
  signal?: AbortSignal
): Promise<ServerCapabilities> {
  try {
    const response = await fetch("/api/v1/servers/local/capabilities", { signal });
    if (!response.ok) {
      return fallbackCapabilities;
    }
    return (await response.json()) as ServerCapabilities;
  } catch {
    return fallbackCapabilities;
  }
}

export async function getPalDefenderStatus(
  serverId = "local",
  signal?: AbortSignal
): Promise<PalDefenderStatus> {
  return getPalDefenderResource("status", "PalDefender 状态读取失败", signal, serverId);
}

export async function getPalDefenderCatalog(
  serverId = "local",
  signal?: AbortSignal
): Promise<PalDefenderCatalog> {
  return getPalDefenderResource("catalog", "PalDefender 接口目录读取失败", signal, serverId);
}

export async function getPalDefenderPlayers(
  signal?: AbortSignal
): Promise<PalDefenderPlayers> {
  return getPalDefenderResource("players", "PalDefender 玩家列表读取失败", signal);
}

export async function getPalDefenderPlayer(
  playerIdentifier: string,
  signal?: AbortSignal
): Promise<PalDefenderPlayer> {
  return getPalDefenderResource(
    `player/${encodeURIComponent(playerIdentifier)}`,
    "PalDefender 玩家详情读取失败",
    signal
  );
}

export async function getPalDefenderProgression(
  playerIdentifier: string,
  signal?: AbortSignal
): Promise<PalDefenderProgression> {
  return getPalDefenderResource(
    `progression/${encodeURIComponent(playerIdentifier)}`,
    "PalDefender 玩家进度读取失败",
    signal
  );
}

export async function getPalDefenderItems(
  playerIdentifier: string,
  signal?: AbortSignal
): Promise<PalDefenderItems> {
  return getPalDefenderResource(
    `items/${encodeURIComponent(playerIdentifier)}`,
    "PalDefender 玩家物品读取失败",
    signal
  );
}

export async function getPalDefenderPals(
  playerIdentifier: string,
  signal?: AbortSignal
): Promise<PalDefenderPals> {
  return getPalDefenderResource(
    `pals/${encodeURIComponent(playerIdentifier)}`,
    "PalDefender 玩家帕鲁读取失败",
    signal
  );
}

export async function getPalDefenderTechs(
  playerIdentifier: string,
  signal?: AbortSignal
): Promise<PalDefenderTechs> {
  return getPalDefenderResource(
    `techs/${encodeURIComponent(playerIdentifier)}`,
    "PalDefender 玩家科技读取失败",
    signal
  );
}

export async function getPalDefenderGuilds(
  signal?: AbortSignal
): Promise<PalDefenderGuilds> {
  return getPalDefenderResource("guilds", "PalDefender 公会列表读取失败", signal);
}

export async function getPalDefenderGuild(
  guildId: string,
  signal?: AbortSignal
): Promise<PalDefenderGuild> {
  return getPalDefenderResource(
    `guild/${encodeURIComponent(guildId)}`,
    "PalDefender 公会详情读取失败",
    signal
  );
}

export async function getPalDefenderBanlist(
  query: PalDefenderBanlistQuery = {},
  signal?: AbortSignal
): Promise<PalDefenderBanlist> {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (value !== undefined && value !== "") {
      search.set(key, String(value));
    }
  }
  const queryString = search.toString();
  return getPalDefenderResource(
    `banlist${queryString ? `?${queryString}` : ""}`,
    "PalDefender 封禁记录读取失败",
    signal
  );
}

export async function submitPalDefenderCommand(
  input: SubmitPalDefenderCommandInput
): Promise<PalDefenderCommand> {
  const url = buildPalDefenderResourceUrl(input.path, input.serverId ?? "local");
  const response = await fetch(url, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "Idempotency-Key": input.idempotencyKey
    },
    body: JSON.stringify({
      reason: input.reason,
      payload: input.payload ?? null
    })
  });
  if (!response.ok) {
    throw new Error(await getApiErrorMessage(response, "PalDefender 命令提交失败"));
  }
  return (await response.json()) as PalDefenderCommand;
}

export async function getPalDefenderCommand(
  statusUrl: string,
  signal?: AbortSignal
): Promise<PalDefenderCommand> {
  const url = statusUrl.startsWith("/")
    ? statusUrl
    : `/api/v1/paldefender-commands/${encodeURIComponent(statusUrl)}`;
  const response = await fetch(url, { signal, cache: "no-store" });
  if (!response.ok) {
    throw new Error(await getApiErrorMessage(response, "PalDefender 命令状态读取失败"));
  }
  return (await response.json()) as PalDefenderCommand;
}

export async function getPalDefenderAudit(
  limit = 100,
  signal?: AbortSignal
): Promise<PalDefenderAuditEvent[]> {
  const safeLimit = Math.max(1, Math.min(1000, Math.trunc(limit)));
  const response = await fetch(
    `/api/v1/audit/paldefender-commands?limit=${safeLimit}`,
    { signal, cache: "no-store" }
  );
  if (!response.ok) {
    throw new Error(await getApiErrorMessage(response, "PalDefender 审计记录读取失败"));
  }
  const payload = (await response.json()) as { items?: PalDefenderAuditEvent[] };
  return payload.items ?? [];
}

export async function getGameResourceCatalog(
  serverId = "local",
  signal?: AbortSignal
): Promise<GameResourceCatalog> {
  const response = await fetch(
    `/api/v1/servers/${encodeURIComponent(serverId)}/game-catalog`,
    { signal, cache: "no-cache" }
  );
  if (!response.ok) {
    throw new Error(await getApiErrorMessage(response, "游戏资源目录读取失败"));
  }
  return (await response.json()) as GameResourceCatalog;
}

async function getPalDefenderResource<T>(
  path: string,
  fallbackMessage: string,
  signal?: AbortSignal,
  serverId = "local"
): Promise<T> {
  const response = await fetch(buildPalDefenderResourceUrl(path, serverId), {
    signal,
    cache: "no-store"
  });
  if (!response.ok) {
    throw new Error(await getApiErrorMessage(response, fallbackMessage));
  }
  return (await response.json()) as T;
}

function buildPalDefenderResourceUrl(path: string, serverId: string): string {
  const relativePath = path.replace(/^\/+/, "");
  if (
    relativePath.length === 0
    || /^https?:/i.test(relativePath)
    || relativePath.split("/").some((part) => part === "." || part === "..")
  ) {
    throw new Error("PalDefender 接口路径无效");
  }
  return `/api/v1/servers/${encodeURIComponent(serverId)}/paldefender/${relativePath}`;
}

export async function getNativePlayerProbe(
  signal?: AbortSignal
): Promise<NativePlayerProbe | null> {
  try {
    const response = await fetch(
      "/api/v1/servers/local/players/native-probe",
      { signal }
    );
    if (!response.ok) {
      return null;
    }
    return (await response.json()) as NativePlayerProbe;
  } catch {
    return null;
  }
}

export async function getPlayers(signal?: AbortSignal): Promise<PlayerSummary[]> {
  try {
    const response = await fetch("/api/v1/servers/local/players", { signal });
    if (!response.ok) {
      return [];
    }
    const payload = (await response.json()) as { items: PlayerSummary[] };
    return payload.items;
  } catch {
    return [];
  }
}

export async function getPlayerProgression(
  playerId: string,
  signal?: AbortSignal
): Promise<PlayerProgression> {
  const response = await fetch(
    `/api/v1/servers/local/players/${encodeURIComponent(playerId)}/progression`,
    { signal, cache: "no-store" }
  );
  if (!response.ok) {
    throw new Error(await getApiErrorMessage(response, "玩家成长数据读取失败"));
  }
  return (await response.json()) as PlayerProgression;
}

export async function mutatePlayerProgression(input: {
  playerId: string;
  expectedRevision: string;
  reason: string;
  dryRun: boolean;
  patch: PlayerProgressionPatch;
}): Promise<PlayerProgressionMutationResult> {
  const response = await fetch(
    `/api/v1/servers/local/players/${encodeURIComponent(input.playerId)}/progression/mutations`,
    {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "Idempotency-Key": crypto.randomUUID()
      },
      body: JSON.stringify({
        expectedRevision: input.expectedRevision,
        reason: input.reason,
        dryRun: input.dryRun,
        patch: input.patch
      })
    }
  );
  if (!response.ok) {
    throw new Error(await getApiErrorMessage(response, "玩家成长修改失败"));
  }
  return (await response.json()) as PlayerProgressionMutationResult;
}

export async function getLiveMapSnapshot(
  serverId = "local",
  signal?: AbortSignal
): Promise<LiveMapSnapshot> {
  const response = await fetch(
    `/api/v1/servers/${encodeURIComponent(serverId)}/live-map`,
    { signal, cache: "no-store" }
  );
  if (!response.ok) {
    throw new Error(await getApiErrorMessage(response, "实时地图读取失败"));
  }
  const snapshot = parseLiveMapSnapshot(await response.json());
  if (!snapshot) {
    throw new Error("实时地图返回了无法识别的数据");
  }
  return snapshot;
}

export function getLiveMapEventsUrl(serverId = "local"): string {
  return `/api/v1/servers/${encodeURIComponent(serverId)}/live-map/events`;
}

export function parseLiveMapSnapshot(payload: unknown): LiveMapSnapshot | null {
  if (!payload || typeof payload !== "object") {
    return null;
  }

  const envelope = payload as Record<string, unknown>;
  const candidate = isRecord(envelope.snapshot)
    ? envelope.snapshot
    : isRecord(envelope.data)
      ? envelope.data
      : envelope;
  if (!Array.isArray(candidate.items) || !isRecord(candidate.coordinateSpace)) {
    return null;
  }

  const coordinateSpace = candidate.coordinateSpace;
  if (!isRecord(coordinateSpace.bounds) || !isRecord(coordinateSpace.projection)) {
    return null;
  }

  const bounds = coordinateSpace.bounds;
  const projection = coordinateSpace.projection;
  const items = candidate.items
    .filter(isRecord)
    .map((item): LiveMapPlayer | null => {
      if (!isRecord(item.position)) {
        return null;
      }
      const x = finiteNumber(item.position.x);
      const y = finiteNumber(item.position.y);
      const playerId = stringValue(item.playerId);
      if (x === null || y === null || !playerId) {
        return null;
      }
      return {
        playerId,
        uid: nullableString(item.uid),
        name: stringValue(item.name) || playerId,
        level: finiteNumber(item.level),
        online: typeof item.online === "boolean" ? item.online : true,
        observedAt: nullableString(item.observedAt),
        position: { x, y }
      };
    })
    .filter((item): item is LiveMapPlayer => item !== null);

  const minX = finiteNumber(bounds.minX);
  const maxX = finiteNumber(bounds.maxX);
  const minY = finiteNumber(bounds.minY);
  const maxY = finiteNumber(bounds.maxY);
  if (minX === null || maxX === null || minY === null || maxY === null) {
    return null;
  }

  const rawStatus = stringValue(candidate.status);
  const status: LiveMapStatus = rawStatus === "live" || rawStatus === "stale" || rawStatus === "unavailable"
    ? rawStatus
    : "unavailable";
  const generatedAt = stringValue(candidate.generatedAt) || new Date().toISOString();
  const sampleIntervalMs = positiveNumber(candidate.sampleIntervalMs, 1_000);
  const staleAfterMs = positiveNumber(candidate.staleAfterMs, Math.max(3_000, sampleIntervalMs * 3));
  const unavailableAfterMs = positiveNumber(candidate.unavailableAfterMs, Math.max(15_000, staleAfterMs * 3));

  return {
    serverId: stringValue(candidate.serverId) || "local",
    streamId: stringValue(candidate.streamId) || "legacy",
    sequence: finiteNumber(candidate.sequence) ?? 0,
    status,
    source: stringValue(candidate.source) || "official-rest",
    observedAt: nullableString(candidate.observedAt),
    generatedAt,
    sampleIntervalMs,
    staleAfterMs,
    unavailableAfterMs,
    coordinateSpace: {
      mapId: stringValue(coordinateSpace.mapId) || "main-world",
      units: stringValue(coordinateSpace.units) || "unreal-centimeters",
      bounds: { minX, maxX, minY, maxY },
      projection: {
        axisSwap: typeof projection.axisSwap === "boolean" ? projection.axisSwap : true,
        invertX: typeof projection.invertX === "boolean" ? projection.invertX : true,
        invertY: typeof projection.invertY === "boolean" ? projection.invertY : false
      },
      backgroundUrl: nullableString(coordinateSpace.backgroundUrl)
    },
    items
  };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}

function finiteNumber(value: unknown): number | null {
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}

function positiveNumber(value: unknown, fallback: number): number {
  const number = finiteNumber(value);
  return number !== null && number > 0 ? number : fallback;
}

function stringValue(value: unknown): string {
  return typeof value === "string" ? value : "";
}

function nullableString(value: unknown): string | null {
  return typeof value === "string" && value.length > 0 ? value : null;
}

export async function getSaveStatus(
  serverId = "local",
  signal?: AbortSignal
): Promise<SaveStatus> {
  const response = await fetch(
    `/api/v1/servers/${encodeURIComponent(serverId)}/saves/status`,
    { signal, cache: "no-store" }
  );
  if (!response.ok) {
    throw new Error(await getApiErrorMessage(response, "存档状态读取失败"));
  }
  const status = parseSaveStatus(await response.json());
  if (!status) {
    throw new Error("存档状态返回了无法识别的数据");
  }
  return status;
}

export async function listSaveBackups(
  serverId = "local",
  kind?: BackupKind,
  signal?: AbortSignal
): Promise<SaveBackup[]> {
  const query = kind ? `?kind=${encodeURIComponent(kind)}` : "";
  const response = await fetch(
    `/api/v1/servers/${encodeURIComponent(serverId)}/backups${query}`,
    { signal, cache: "no-store" }
  );
  if (!response.ok) {
    throw new Error(await getApiErrorMessage(response, "备份列表读取失败"));
  }
  const payload = await response.json() as unknown;
  const envelope = isRecord(payload) ? payload : null;
  const data = isRecord(envelope?.data) ? envelope.data : null;
  const items = Array.isArray(payload)
    ? payload
    : Array.isArray(envelope?.items)
      ? envelope.items
      : Array.isArray(envelope?.data)
        ? envelope.data
        : Array.isArray(data?.items)
          ? data.items
          : [];
  return items
    .map((item) => parseSaveBackup(item, kind))
    .filter((item): item is SaveBackup => item !== null);
}

export async function getSaveBackup(
  serverId: string,
  backupId: string,
  signal?: AbortSignal
): Promise<SaveBackup> {
  const response = await fetch(
    `/api/v1/servers/${encodeURIComponent(serverId)}/backups/${encodeURIComponent(backupId)}`,
    { signal, cache: "no-store" }
  );
  if (!response.ok) {
    throw new Error(await getApiErrorMessage(response, "备份详情读取失败"));
  }
  const backup = parseSaveBackup(await response.json());
  if (!backup) {
    throw new Error("备份详情返回了无法识别的数据");
  }
  return backup;
}

export async function flushWorldSave(
  serverId: string,
  reason: string,
  idempotencyKey: string
): Promise<SaveCommand> {
  return postSaveCommand(
    `/api/v1/servers/${encodeURIComponent(serverId)}/saves/flush`,
    { reason },
    idempotencyKey,
    "保存世界命令提交失败"
  );
}

export async function createSaveBackup(
  serverId: string,
  input: { label: string; reason: string },
  idempotencyKey: string
): Promise<SaveCommand> {
  return postSaveCommand(
    `/api/v1/servers/${encodeURIComponent(serverId)}/backups`,
    input,
    idempotencyKey,
    "备份创建命令提交失败"
  );
}

export async function verifySaveBackup(
  serverId: string,
  backupId: string,
  reason: string,
  idempotencyKey: string
): Promise<SaveCommand> {
  return postSaveCommand(
    `/api/v1/servers/${encodeURIComponent(serverId)}/backups/${encodeURIComponent(backupId)}/verify`,
    { reason },
    idempotencyKey,
    "备份校验命令提交失败"
  );
}

export async function getSaveCommand(
  commandOrStatusUrl: string,
  signal?: AbortSignal
): Promise<SaveCommand> {
  const statusUrl = commandOrStatusUrl.startsWith("/") || /^https?:\/\//i.test(commandOrStatusUrl)
    ? commandOrStatusUrl
    : `/api/v1/save-commands/${encodeURIComponent(commandOrStatusUrl)}`;
  const response = await fetch(statusUrl, { signal, cache: "no-store" });
  if (!response.ok) {
    throw new Error(await getApiErrorMessage(response, "存档命令状态读取失败"));
  }
  const command = parseSaveCommand(await response.json());
  if (!command) {
    throw new Error("存档命令返回了无法识别的数据");
  }
  return command;
}

async function postSaveCommand(
  url: string,
  input: Record<string, string>,
  idempotencyKey: string,
  fallbackMessage: string
): Promise<SaveCommand> {
  const response = await fetch(url, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "Idempotency-Key": idempotencyKey
    },
    body: JSON.stringify(input)
  });
  if (!response.ok) {
    throw new Error(await getApiErrorMessage(response, fallbackMessage));
  }
  const command = parseSaveCommand(await response.json());
  if (!command) {
    throw new Error("存档命令返回了无法识别的数据");
  }
  return command;
}

function parseSaveStatus(payload: unknown): SaveStatus | null {
  const root = unwrapRecord(payload);
  if (!root) {
    return null;
  }
  const save = isRecord(root.save) ? root.save : {};
  const disk = isRecord(root.disk) ? root.disk : {};
  const nativeBackups = isRecord(root.nativeBackups) ? root.nativeBackups : {};
  const managedBackups = isRecord(root.managedBackups) ? root.managedBackups : {};
  const validation = isRecord(root.validation) ? root.validation : {};
  return {
    serverId: stringValue(root.serverId) || "local",
    ready: root.ready === true,
    checkedAt: stringValue(root.checkedAt) || new Date().toISOString(),
    worldGuid: nullableString(root.worldGuid),
    worldName: nullableString(root.worldName),
    gameVersion: nullableString(root.gameVersion),
    onlinePlayerCount: finiteNumber(root.onlinePlayerCount),
    save: {
      fileCount: safeNumber(save.fileCount),
      playerFileCount: safeNumber(save.playerFileCount),
      totalBytes: safeNumber(save.totalBytes),
      lastModifiedAt: nullableString(save.lastModifiedAt)
    },
    disk: {
      availableBytes: safeNumber(disk.availableBytes),
      totalBytes: safeNumber(disk.totalBytes)
    },
    nativeBackups: {
      count: safeNumber(nativeBackups.count),
      totalBytes: safeNumber(nativeBackups.totalBytes),
      latestCreatedAt: nullableString(nativeBackups.latestCreatedAt)
    },
    managedBackups: {
      count: safeNumber(managedBackups.count),
      totalBytes: safeNumber(managedBackups.totalBytes),
      verifiedCount: safeNumber(managedBackups.verifiedCount),
      latestCreatedAt: nullableString(managedBackups.latestCreatedAt)
    },
    validation: {
      processPathMatched: validation.processPathMatched === true,
      serverNameMatched: validation.serverNameMatched === true,
      worldGuidMatched: validation.worldGuidMatched === true
    },
    error: parseApiProblem(root.error)
  };
}

function parseSaveBackup(payload: unknown, fallbackKind?: BackupKind): SaveBackup | null {
  const root = unwrapRecord(payload);
  if (!root) {
    return null;
  }
  const backupId = stringValue(root.backupId) || stringValue(root.id);
  if (!backupId) {
    return null;
  }
  const rawKind = stringValue(root.kind).toLocaleLowerCase();
  const kind: BackupKind = rawKind === "native" || rawKind === "game"
    ? "native"
    : rawKind === "managed" || rawKind === "pal-control"
      ? "managed"
      : fallbackKind ?? "managed";
  return {
    backupId,
    kind,
    label: nullableString(root.label),
    worldGuid: nullableString(root.worldGuid),
    gameVersion: nullableString(root.gameVersion),
    createdAt: stringValue(root.createdAt) || new Date(0).toISOString(),
    fileCount: safeNumber(root.fileCount),
    totalBytes: safeNumber(root.totalBytes),
    integrity: stringValue(root.integrity) || "unknown",
    consistency: stringValue(root.consistency) || "unknown",
    actor: nullableString(root.actor),
    reason: nullableString(root.reason),
    manifestSha256: nullableString(root.manifestSha256)
  };
}

function parseSaveCommand(payload: unknown): SaveCommand | null {
  const root = unwrapRecord(payload);
  if (!root) {
    return null;
  }
  const commandId = stringValue(root.commandId) || stringValue(root.id);
  if (!commandId) {
    return null;
  }
  return {
    commandId,
    type: stringValue(root.type) || "save",
    state: stringValue(root.state) || "queued",
    stage: stringValue(root.stage) || stringValue(root.state) || "queued",
    createdAt: stringValue(root.createdAt) || new Date().toISOString(),
    completedAt: nullableString(root.completedAt),
    statusUrl: stringValue(root.statusUrl) || `/api/v1/save-commands/${encodeURIComponent(commandId)}`,
    backupId: nullableString(root.backupId),
    result: isRecord(root.result) ? root.result : null,
    error: parseApiProblem(root.error)
  };
}

function unwrapRecord(payload: unknown): Record<string, unknown> | null {
  if (!isRecord(payload)) {
    return null;
  }
  if (isRecord(payload.data)) {
    return payload.data;
  }
  if (isRecord(payload.item)) {
    return payload.item;
  }
  return payload;
}

function parseApiProblem(value: unknown): ApiProblem | null {
  if (typeof value === "string" && value) {
    return { code: "SAVE_OPERATION_FAILED", message: value };
  }
  if (!isRecord(value)) {
    return null;
  }
  const message = stringValue(value.message) || stringValue(value.detail);
  if (!message) {
    return null;
  }
  return {
    code: stringValue(value.code) || "SAVE_OPERATION_FAILED",
    message
  };
}

function safeNumber(value: unknown): number {
  const number = finiteNumber(value);
  return number !== null && number >= 0 ? number : 0;
}

export async function getNativePlayerSchema(
  signal?: AbortSignal
): Promise<NativePlayerSchema | null> {
  try {
    const response = await fetch(
      "/api/v1/servers/local/players/native-schema",
      { signal }
    );
    if (!response.ok) {
      return null;
    }
    return (await response.json()) as NativePlayerSchema;
  } catch {
    return null;
  }
}

export async function getNativeInventorySchema(
  signal?: AbortSignal
): Promise<NativeInventorySchema | null> {
  try {
    const response = await fetch(
      "/api/v1/servers/local/inventory/native-schema",
      { signal }
    );
    if (!response.ok) {
      return null;
    }
    return (await response.json()) as NativeInventorySchema;
  } catch {
    return null;
  }
}

export async function getNativeInventoryProbe(
  signal?: AbortSignal
): Promise<NativeInventoryProbe | null> {
  try {
    const response = await fetch(
      "/api/v1/servers/local/inventory/native-probe",
      { signal }
    );
    if (!response.ok) {
      return null;
    }
    return (await response.json()) as NativeInventoryProbe;
  } catch {
    return null;
  }
}

export async function setInventoryQuantity(input: {
  ownerPlayerId: string;
  containerId: string;
  containerKind: string;
  slotIndex: number;
  itemId: string;
  expectedQuantity: number;
  quantity: number;
  reason: string;
  dryRun: boolean;
}): Promise<InventoryMutationResult> {
  const response = await fetch(
    `/api/v1/servers/local/players/${encodeURIComponent(input.ownerPlayerId)}/inventory/transactions`,
    {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "Idempotency-Key": globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random()}`
      },
      body: JSON.stringify({
        expectedRevision: 0,
        reason: input.reason,
        dryRun: input.dryRun,
        operations: [{
          type: "setQuantity",
          itemId: input.itemId,
          quantity: input.quantity,
          slotId: `${input.containerKind}:${input.slotIndex}`,
          durability: null,
          containerId: input.containerId,
          expectedQuantity: input.expectedQuantity
        }]
      })
    }
  );

  if (!response.ok) {
    const error = await response.json().catch(() => null) as {
      code?: string;
      message?: string;
    } | null;
    throw new Error(error?.message ?? `物品数量修改失败（HTTP ${response.status}）`);
  }
  return (await response.json()) as InventoryMutationResult;
}

export async function getNativePalProbe(
  signal?: AbortSignal
): Promise<NativePalProbe | null> {
  try {
    const response = await fetch(
      "/api/v1/servers/local/pals/native-probe",
      { signal }
    );
    if (!response.ok) {
      return null;
    }
    return (await response.json()) as NativePalProbe;
  } catch {
    return null;
  }
}

export async function getPalSkillCatalog(
  signal?: AbortSignal
): Promise<PalSkillCatalog | null> {
  try {
    const response = await fetch(
      "/api/v1/servers/local/pals/skill-catalog",
      { signal }
    );
    if (!response.ok) {
      return null;
    }
    return (await response.json()) as PalSkillCatalog;
  } catch {
    return null;
  }
}

export async function mutatePal(
  pal: NativePal,
  input: PalMutationInput
): Promise<PalMutationResult> {
  const response = await fetch(
    `/api/v1/servers/local/players/${encodeURIComponent(pal.ownerPlayerUId)}/pals/${encodeURIComponent(pal.instanceId)}/mutations`,
    {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "Idempotency-Key": globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random()}`
      },
      body: JSON.stringify({
        expectedRevision: pal.revision,
        reason: input.reason,
        requireState: "loaded",
        dryRun: input.dryRun,
        patch: {
          nickname: input.nickname,
          favorite: input.favorite,
          passiveSkill: input.passiveSkill ?? null,
          equippedActiveSkills: input.equippedActiveSkills ?? null
        }
      })
    }
  );

  if (!response.ok) {
    const error = await response.json().catch(() => null) as {
      code?: string;
      message?: string;
    } | null;
    throw new Error(error?.message ?? `帕鲁修改失败（HTTP ${response.status}）`);
  }
  return (await response.json()) as PalMutationResult;
}
