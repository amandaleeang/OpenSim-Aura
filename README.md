# OpenSim-Aura

OpenSim-Aura is a fork of [OpenSimulator](https://github.com/opensim/opensim) (OpenSim).

OpenSim-Aura is **alpha**, the same as stock OpenSim. It is provided **as is**. Like any alpha software it is experimental and may have bugs and security issues. Do not install it on a local machine or network that holds sensitive data. Use a container or a dedicated server.

Aura focuses on Hypergrid travel, asset transfer, baked textures (XBakes and Firestorm server-side bake), profiles, attachment scripts, friends, IM, display names, UUID DHT lookup, HG groups, and SQLite standalone backends — the parts that are slow, missing, or broken when avatars move between grids.

Binaries are on the [Releases](https://github.com/amandaleeang/OpenSim-Aura/releases) page. To build from source, see [BUILDING.md](BUILDING.md). For installing, running, and configuring OpenSim itself, see [opensimulator.org](http://opensimulator.org).

# What Aura implements

Stock Hypergrid often does one HTTP GET per asset, fully sequential. Hundreds of attachment textures and meshes then take minutes. Profiles of foreign avatars fail. Bakes greyscale on every hop because TextureIDs change. Firestorm 7 stays a cloud unless the sim speaks SSA. Attachment scripts stay dead until detach/reattach. Friends made abroad vanish after logout. Private HG IMs cannot be answered. Display names do not carry. Foreign UUIDs have no locator. Groups V2, Offline IM V2, and FSAssets did not persist on SQLite. Aura addresses those.

## Concurrent asset gather

Incoming assets are fetched in concurrent waves instead of one-at-a-time HTTP GETs.

Measured on 587 HG attachment assets from OSGrid:

| Mode | Time |
|------|------|
| Sequential | ~173–201 s |
| 8 concurrent | ~40 s |
| 16 concurrent | ~21 s |

The same gather is used for:

- HG login / teleport (incoming attachments)
- Buy / open object
- Copying inventory, giving a folder or item
- Rezzing attachments
- Taking items from a prim
- Asset gather from Robust
- OAR and IAR writes
- Flotsam region cache and appearance-info walks

HG asset Get/Post can batch several inventory roots in one pass. Missing assets at the destination are stored in waves. Nested item assets are prefetched on inventory operations. Remote folder contents can be asked in one `GetMultipleFoldersContent` call, with concurrent per-folder fallback if the batch is empty or the far grid restricts inventory.

On HG import, after inspect, one `AssetsExist` check skips blob GETs for textures, sounds, and meshes already on this grid. Missing leaves still fetch from home. Repeat HG logins therefore do not re-download the same outfit. OAR/IAR and Flotsam cache walks still fetch everything.

If a wave times out, in-flight GETs are not marked failed. A drain thread inspects those completions so nested assets still land in cache. Sequential gather no longer fetches inspect-queue UUIDs a second time.

HG visitor **viewer** fetches (inventory open/play and UDP textures) stay in the simulator cache only, matching CAPS `GetAsset`. They are not written to the local asset DB.

## XBakes (baked textures)

- Bakes stored by deterministic **CacheId**, not only TextureID (TextureIDs are not canonical across hops).
- On CacheId match, stored JPEG2000 is re-keyed to the incoming TextureID so the same outfit does not rebake on homecoming.
- XBakes runs for **HG visitors** and for **avatars returning home** (`Validate` on ViaHGLogin; appearance is not saved for foreign users).
- **Standalone** (no Robust): when `[XBakes] URL` is unset, an in-process file store under `BaseDirectory` uses the same XML and hashed paths as Robust XBakes.

## Server-side baked textures (Firestorm SSA)

When `[ServerSideBake] Enabled` is true (Aura default), the region sets protocol bit 0. Firestorm 7.2 then stops uploading local bakes and GET JPEG2000 from `/appearance/texture/...` on the **connected sim**. There is no Linden bake farm. Bakes stay in cache / XBakes, not the asset DB.

The compositor layers Current Outfit wearables. Bake size is 512 / 1024 / 2048 from the largest layer on that body part; eyes stay 128. Rebake Textures (`IncrementCOFVersion`) force-renders. Outfit changes and leaving appearance edit reuse layer hashes.

Default bodypaint matches the Firestorm library bake (skin tint × grain, then colour TGA). HG visitors have no Current Outfit folder here; incoming Firestorm bakes are kept instead of compositing one tattoo per wearable type.

## UUID DHT (Hypergrid UUID locator)

On **Hypergrid only**. After a GridUser or Groups SQL miss, the node FIND the UUID on a signed overlay and then calls the home grid (`get_user_info`, `GETGROUP`). Intra-grid Robust and plain standalone do not use it: a SQL miss means the UUID is not local.

StandaloneHypergrid and Robust.HG load the `UuidDht*` modules; that turns DHT on. `[UuidDht] Enabled` is not required. Public bootstrap seeds are `http://207.180.199.55:10001/` and `http://207.180.199.55:10000/`. If `config-include/dht-seeds` is missing, it is copied from `dht-seeds.example` on first start.

HomeURI must be a public URL (not `127.0.0.1`). Keep `uuiddht/identity-{port}.json`; losing it is a new node, not a move.

## Hypergrid groups

- Groups V2 uses `[Groups] LocalServiceModule` like UserAccountService. Empty means groups stay off.
- `GroupsExternalURI` defaults to this grid’s HomeURI (not `127.0.0.1`), so joining a foreign group does not store a loopback Location.
- `GroupProfileHGAccess` (`open` / `closed` / `token`, default `open`) is HG `GETGROUP` on `/hg-groups` only. Local `/groups` is unchanged. Join, members, roles, and notices still need a membership token.
- `ShowUserDetailsInHGProfile` defaults true.
- The viewer is not sent `"Unknown"` as a group name (Firestorm caches that for a week). The real name is pushed from GroupProfile.

## Hypergrid profiles

- **HG visitor profiles** load even when `get_user_info` / HomeURI cache fails; fallback to HomeURL and re-query `get_server_urls`.
- **Self profile** on a foreign sim uses the agent circuit `ProfileServerURI`; failed responses are not cached.
- **Friends’ profiles:** seed UserManagement from the visitor’s home friends list; ask the visitor’s home `get_uui` for a friend’s home URL so OSGrid (and similar) friends are fetched from the right grid, not from the visitor’s profile host.

## Attachment scripts on HG login / teleport

On HG entry, `CompleteMovement` often finishes before async asset gather. Scripts start exactly once when the agent is already root and the incoming set is complete — both the batch handshake and individual `IncomingCreateObject`. The same path runs for HG visitors and for local users coming home. The handshake resets when the agent becomes a child again so a later return to the region can start scripts.

If the circuit is missing or `AssetServerURI` is empty / slash-only, attachment add falls through like scene objects instead of dropping the batch.

Related upstream fix: scripts are marked **HasRun** on event dispatch so teleport does not serialize empty script state.

## Hypergrid IM

Outgoing HG IMs stamp the sender as `uuid;homeURI;name` (same form as friends and creators). Extra XML-RPC keys are ignored by stock OpenSim. On receive, UserManagement and GridUser are seeded from that UUI, so reply and profile work without friendship. `get_uui` also returns GridUser contacts so this still works after teleport.

No extra INI keys. HG standalone still needs `[Messaging] OfflineIMService` if offline IMs should be stored (see below).

## Hypergrid friends

- Session-proven HG friendships are stored as **accepted** (not pending), so friends made abroad remain after logout.
- Travelers with a zero presence `RegionID` are treated as online (`LocateUser` / home probe) instead of disappearing from the friends list.
- Status notify collects every `friend_*` field, including gapped indexes from remotes that do not send a dense `friend_0..N` sequence.
- A verified re-accept with a new secret **replaces** the stored UUI so online status still matches.

## Display names

SL-compatible display names on `UserAccount`. Viewers use `GetDisplayNames` / `SetDisplayName`. The name is carried on the HG circuit and `get_user_info`, so foreign sims show it without a home round-trip after the first hop.

- Max 31 characters. Not unique.
- HG visitors cannot set a name here (`foreign_grid`); they change it at home.
- Viewer set/reset is cooldown-gated (`[DisplayNames] ChangeCooldownDays`). Console commands are not.
- Database columns are added by the usual UserAccount migrations on first start.

## SQLite standalone backends

SQLite can now persist the services that previously required MySQL or PostgreSQL:

- **FSAssets** — asset bytes on disk, metadata in `Asset.db`.
- **Groups Module V2** — groups, membership, roles, notices.
- **Offline Message Module V2** — offline IMs.

All SQLite connections use WAL, `busy_timeout=30000`, `synchronous=NORMAL`, and an in-memory temp store. `OpenSim.db` and `Asset.db` also get a 64MiB page cache and checkpoint-truncate on close.

# Bugs fixed

- **Attachment scripts stayed dead** after HG login/teleport when gather finished while the agent was still a child, or when attachments arrived one object at a time. Scripts now start once the agent is root.
- **HG attachment add was dropped** when the circuit was null or `AssetServerURI` was missing/slash-only.
- **Nested assets vanished** when a concurrent gather wave timed out: in-flight GETs were marked failed. They now drain and inspect into cache.
- **Sequential HG gather fetched inspect assets twice.**
- **HG visitor UDP textures and inventory open/play** wrote foreign assets into the local DB. They now stay in cache, like CAPS `GetAsset`.
- **HGAssetMapper was process-wide static**, so a multi-region sim rewrote object XML with the last-loaded region’s name and scope. It is per-region.
- **GetMultipleItems** packed cache hits at the front of the result array, so callers that treat `result[i]` as `ids[i]` received the wrong items.
- **Friends made while traveling** were stored pending (`flags=0`) and vanished after logout.
- **HG travelers appeared offline** to friends because presence `RegionID` is zero after teleport.
- **Friend status notify stopped** at the first missing `friend_N` and ignored gapped indexes.
- **Re-accepting an HG friendship** left the old secret in place, so later status notify never matched.
- **Private HG IMs** could not be answered or have their profile opened without friendship.
- **Creating a group** left the viewer on a fake “new group” row; the real name is now pushed with `AgentGroupDataUpdate`.
- **Firestorm cached `"Unknown"`** as a group name for up to a week after a miss.
- **YEngine script state** was dropped on a migration version mismatch; it is restored.
- **Map tiles** ignored PBR terrain textures.
- **Free for-sale parcels** were not marked on the land overlay.
- **Estate DenyIdentified / DenyTransacted** were not sent to the viewer.

# In progress / planned

- Allow HG teleport to a sim with the **same SIM coordinates**.
- **HOP** teleports that land at the coordinates in the URI.
- **Group messages** via Hypergrid.

Not a priority: async HTTP server.

# Aura configuration

Everything else (database, regions, Hypergrid URIs, groups, viewers, ports) is stock OpenSim: [Configuration](http://opensimulator.org/wiki/Configuration) and [Configuring Regions](http://opensimulator.org/wiki/Configuring_Regions).

Aura adds or documents the following INI settings. The sections below are the new or Aura-specific knobs; stock OpenSim keys are unchanged.

## Architecture (Hypergrid vs not)

In `OpenSim.ini` `[Architecture]`, pick **one**:

```
Include-Architecture = "config-include/StandaloneHypergrid.ini"
```

or for a Robust grid, copy `Robust.HG.ini.example` to `Robust.ini` (not `Robust.ini.example` / intra-grid).

| Architecture | DHT | Notes |
|--------------|-----|--------|
| `StandaloneHypergrid.ini` | on | Loads `UuidDht*` LocalServiceModules. |
| `Robust.HG.ini.example` | on | Loads `UuidDhtServiceConnector` on the **public** port and `UuidDht*` services. |
| `Standalone.ini` | off | SQL miss = not local. |
| `Robust.ini.example` | off | Intra-grid. Do not copy the HG DHT lines here. |

Hypergrid also needs a **public** HomeURI (`[Const] BaseHostname` is not `127.0.0.1`).

## `[ServerSideBake]` — Firestorm SSA

In `OpenSimDefaults.ini`. Aura default is on.

| Setting | Default | Meaning |
|---------|---------|---------|
| `Enabled` | `true` | RegionHandshake bit 0. Firestorm 7.2 uses UpdateAvatarAppearance and GET `/appearance` instead of uploading local bakes. |

To turn it off in `OpenSim.ini`:

```
[ServerSideBake]
    Enabled = false
```

Standalone still wants `[XBakes] BaseDirectory` so hashes persist across restarts (`StandaloneHypergrid.ini` / `Standalone.ini` set `BaseDirectory = "bakes"`). Grid uses `[XBakes] URL` to Robust BakedTextureService.

## UUID DHT — join the overlay

Loading the `UuidDht*` modules is what turns DHT on. You do **not** set `[UuidDht] Enabled = true`.

**Standalone Hypergrid** already loads them in `StandaloneHypergrid.ini`. Copy `StandaloneCommon.ini.example` → `StandaloneCommon.ini` as usual.

**Robust HG** already loads them in `Robust.HG.ini.example`:

```
[ServiceList]
    UuidDhtServiceConnector = "${Const|PublicPort}/OpenSim.Addons.UUIDDHT.dll:UuidDhtServiceConnector"

[UserAccountService]
    LocalServiceModule = "OpenSim.Addons.UUIDDHT.dll:UuidDhtUserAccountService"

[GridUserService]
    LocalServiceModule = "OpenSim.Addons.UUIDDHT.dll:UuidDhtGridUserService"

[Groups]
    LocalServiceModule = "OpenSim.Addons.UUIDDHT.dll:UuidDhtGroupsService"
```

**Seeds.** On first start, if `config-include/dht-seeds` is missing, the node copies `config-include/dht-seeds.example`:

```
http://207.180.199.55:10001/
http://207.180.199.55:10000/
```

Those two URLs are the public bootstrap. After join, the node rewrites `dht-seeds` (this server first, then admitted peers). Do not commit the living `dht-seeds` file or `uuiddht/`. Keep `uuiddht/identity-{port}.json`.

Optional knobs (defaults in `OpenSimDefaults.ini`):

| Setting | Default | Meaning |
|---------|---------|---------|
| `SeedsPath` | `config-include/dht-seeds` | Living seed list. |
| `RpcTimeoutMs` | `2000` | HTTP POST `/dht/rpc`. |
| `LookupTimeoutMs` | `8000` | One FIND. |
| `ClientLookupCapMs` | `12000` | FIND uuid then FIND node. |
| `HomeHttpTimeoutSec` | `5` | Home `GetUserInfo` / `GETGROUP` after a DHT hit. |
| `ExpireHours` | `36` | Replica TTL unless republished. |

Group UUIDs on the DHT also need Groups V2 enabled (see below). Overlay handlers are on the **public** HTTP port (`/dht/node`, `/dht/owns`, `/dht/rpc`), not Robust private 8003.

## `[EntityTransfer]` — concurrent gather

In `OpenSim.ini` / `OpenSimDefaults.ini`. Defaults below are the Aura defaults (`ConcurrentAssetGather = true`). The older `HG`-prefixed names are still accepted.

| Setting | Default | Meaning |
|---------|---------|---------|
| `ConcurrentAssetGather` | `true` | Wave-based concurrent gather (HG attachments, rez/open/buy/give, local-grid attachment rez / folder give / take-from-prim, Robust Gets). `false` keeps sequential `GatherNext` + `FetchAsset`. |
| `UuidGatherConcurrent` | `8` | Max concurrent asset requests per gather/post wave. Ignored when `ConcurrentAssetGather` is false. |
| `UuidGatherTimeout` | `30` | Per-request timeout in seconds. A slow request is abandoned without holding the rest of the wave. Late completions still inspect into cache. |

Skipping already-local HG leaf assets is automatic.

## `[XBakes]` — bake store

In `OpenSim.ini`. Standalone and StandaloneHypergrid set `BaseDirectory` in their include files.

| Setting | Meaning |
|---------|---------|
| `URL` | Robust BakedTextureService (grid). Leave unset for standalone. |
| `BaseDirectory` | In-process file store when `URL` is unset (standalone). Same hashed layout as Robust XBakes. Example: `BaseDirectory = "bakes"`. |

Disabled if both `URL` and `BaseDirectory` are unset.

## `[DisplayNames]` — cooldown

In `OpenSim.ini`. `GetDisplayNames` and `SetDisplayName` are served by the region.

| Setting | Default | Meaning |
|---------|---------|---------|
| `ChangeCooldownDays` | `0` | Days a viewer must wait after setting or resetting a display name. `0` or omitted: no cooldown. Console `set user displayname` / `reset user displayname` ignore this. |

Example:

```
[DisplayNames]
    ChangeCooldownDays = 7
```

Console (no cooldown):

```
set user displayname First Last Shown Name
reset user displayname First Last
```

## FSAssets on SQLite (standalone)

Optional. Uncomment **all** of these together in `config-include/StandaloneCommon.ini` (copy from `StandaloneCommon.ini.example`). `BaseDirectory` and `SpoolDirectory` must be on the same filesystem. Leave outbound `HypergridAssetService` as `HGAssetServiceConnector`.

```
[AssetService]
    LocalServiceModule    = "OpenSim.Services.FSAssetService.dll:FSAssetConnector"
    LocalGridAssetService = "OpenSim.Services.FSAssetService.dll:FSAssetConnector"
    BaseDirectory = "./fsassets/data"
    SpoolDirectory = "./fsassets/tmp"
    FallbackService = "OpenSim.Services.AssetService.dll:AssetService"
    DaysBetweenAccessTimeUpdates = 30

[GridService]
    AssetService = "OpenSim.Services.FSAssetService.dll:FSAssetConnector"
```

Hypergrid standalone also needs the public HG face of the same store:

```
[HGAssetService]
    LocalServiceModule = "OpenSim.Services.HypergridService.dll:HGFSAssetService"
```

Metadata uses `[AssetService] ConnectionString` (`Asset.db` in `config-include/storage/SQLiteStandalone.ini`). Bytes go to `BaseDirectory`.

## Groups Module V2 on SQLite

In `OpenSim.ini`. Leave `StorageProvider` commented to inherit `[DatabaseService]` (SQLite, MySQL, or PGSQL).

```
[Groups]
    Enabled = true
    Module = "Groups Module V2"
    LocalServiceModule = "OpenSim.Addons.Groups.dll:GroupsService"
    ServicesConnectorModule = "Groups HG Service Connector"
    LocalService = local
    MessagingEnabled = true
    MessagingModule = "Groups Messaging Module V2"
    ; GroupProfileHGAccess = open
    ; GroupsExternalURI = ""
```

On StandaloneHypergrid / Robust HG, leave `LocalServiceModule` as `OpenSim.Addons.UUIDDHT.dll:UuidDhtGroupsService` (already in those example files) so group UUIDs are published and looked up on the DHT. Use `"OpenSim.Addons.Groups.dll:GroupsService"` for intra-grid. Empty `LocalServiceModule` means groups stay off.

Use `"Groups Local Service Connector"` for standalone non-HG, `"Groups Remote Service Connector"` for a grided sim, and `"Groups HG Service Connector"` with `LocalService = local` (standalone) or `remote` (grid).

`GroupProfileHGAccess` is `open` (default), `closed`, or `token`. `GroupsExternalURI` defaults to HomeURI; do not set `127.0.0.1`.

Default SQLite storage (`SQLiteStandalone.ini`) already points Groups at `osgroups.db`. Override only if you want a different file:

```
[Groups]
    ConnectionString = "URI=file:osgroups.db;UseUTF16Encoding=True"
```

## Offline Message Module V2 on SQLite

In `OpenSim.ini`:

```
[Messaging]
    OfflineMessageModule = "Offline Message Module V2"
```

Hypergrid standalone (InGatekeeper) also needs the in-process service so incoming IMs to offline local users are stored. `Robust.HG.ini.example` has the same key.

```
[Messaging]
    OfflineIMService = "OpenSim.Addons.OfflineIM.dll:OfflineIMService"
```

Default SQLite storage already points Messaging at `offlineim.db`. Override only if you want a different file:

```
[Messaging]
    ConnectionString = "URI=file:offlineim.db;UseUTF16Encoding=True"
```

Leave `StorageProvider` commented to inherit `[DatabaseService]`.

# Bugs, discussions, and new features

**OpenSim-Aura only** — bugs, discussions, and feature requests that belong to the Aura work listed above (concurrent gather, XBakes, server-side bakes, UUID DHT, HG groups, HG profiles, attachment scripts on HG login/teleport, HG IM and friends, display names, SQLite FSAssets / Groups V2 / Offline IM V2, and the in-progress items in that list) go to **this GitHub repo**:

https://github.com/amandaleeang/OpenSim-Aura/issues


**Everything other feature or bug belongs to OpenSim.** Report bugs to the OpenSim project on [Mantis](http://opensimulator.org/mantis/main_page.php). Use [opensimulator.org](http://opensimulator.org) for documentation, discussion, and new-feature process (including the opensim-dev list).
