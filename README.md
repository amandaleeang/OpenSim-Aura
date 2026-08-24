# OpenSim-Aura

OpenSim-Aura is a fork of [OpenSimulator](https://github.com/opensim/opensim) (OpenSim).

Aura focuses on Hypergrid travel, asset transfer, baked textures, profiles, and attachment scripts — the parts that are slow or broken when avatars move between grids.

Binaries are on the [Releases](https://github.com/amandaleeang/OpenSim-Aura/releases) page. To build from source, see [BUILDING.md](BUILDING.md). For installing, running, and configuring OpenSim itself, see [opensimulator.org](http://opensimulator.org).

# What Aura implements

Stock Hypergrid often does one HTTP GET per asset, fully sequential. Hundreds of attachment textures and meshes then take minutes. Profiles of foreign avatars fail. Bakes greyscale on every hop because TextureIDs change. Attachment scripts stay dead until detach/reattach. Aura addresses those.

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

## XBakes (baked textures)

- Bakes stored by deterministic **CacheId**, not only TextureID (TextureIDs are not canonical across hops).
- On CacheId match, stored JPEG2000 is re-keyed to the incoming TextureID so the same outfit does not rebake on homecoming.
- XBakes runs for **HG visitors** and for **avatars returning home** (`Validate` on ViaHGLogin; appearance is not saved for foreign users).
- **Standalone** (no Robust): when `[XBakes] URL` is unset, an in-process file store under `BaseDirectory` uses the same XML and hashed paths as Robust XBakes.

## Hypergrid profiles

- **HG visitor profiles** load even when `get_user_info` / HomeURI cache fails; fallback to HomeURL and re-query `get_server_urls`.
- **Self profile** on a foreign sim uses the agent circuit `ProfileServerURI`; failed responses are not cached.
- **Friends’ profiles:** seed UserManagement from the visitor’s home friends list; ask the visitor’s home `get_uui` for a friend’s home URL so OSGrid (and similar) friends are fetched from the right grid, not from the visitor’s profile host.

## Attachment scripts on HG login / teleport

On HG entry, `CompleteMovement` often finishes before async asset gather. When attachments attach later on a root agent, scripts are started and resumed the same way local teleports do. Local users also resume attachment scripts after HG teleport.

Related upstream fix: scripts are marked **HasRun** on event dispatch so teleport does not serialize empty script state.

# In progress / planned

- Keep HG visitor attachments **only in simulator cache**; promote to the DB asset store on drop or rez (cleaner DB).
- **FSAssets** on SQLite.
- Allow HG teleport to a sim with the **same SIM coordinates**.
- **HOP** teleports that land at the coordinates in the URI.
- **Group messages** via Hypergrid.
- **Display names**. (Evaluating)
- Fetch **only foreign** assets (skip a redundant local probe) — still evaluating whether it is worth it.

Not a priority: async HTTP server.

# Aura configuration

Everything else (database, regions, Hypergrid URIs, groups, viewers, ports) is stock OpenSim: [Configuration](http://opensimulator.org/wiki/Configuration) and [Configuring Regions](http://opensimulator.org/wiki/Configuring_Regions).

Aura adds the following INI settings.

## `[EntityTransfer]` — concurrent gather

In `OpenSim.ini` / `OpenSimDefaults.ini`. Defaults below are the Aura defaults (`ConcurrentAssetGather = true`). The older `HG`-prefixed names are still accepted.

| Setting | Default | Meaning |
|---------|---------|---------|
| `ConcurrentAssetGather` | `true` | Wave-based concurrent gather (HG attachments, rez/open/buy/give, local-grid attachment rez / folder give / take-from-prim, Robust Gets). `false` keeps sequential `GatherNext` + `FetchAsset`. |
| `UuidGatherConcurrent` | `8` | Max concurrent asset requests per gather/post wave. Ignored when `ConcurrentAssetGather` is false. |
| `UuidGatherTimeout` | `30` | Per-request timeout in seconds. A slow request is abandoned without holding the rest of the wave. |

## `[XBakes]` — bake store

In `OpenSim.ini`. Standalone and StandaloneHypergrid set `BaseDirectory` in their include files.

| Setting | Meaning |
|---------|---------|
| `URL` | Robust BakedTextureService (grid). Leave unset for standalone. |
| `BaseDirectory` | In-process file store when `URL` is unset (standalone). Same hashed layout as Robust XBakes. Example: `BaseDirectory = "bakes"`. |

Disabled if both `URL` and `BaseDirectory` are unset.

# Bugs, discussions, and new features

**OpenSim-Aura only** — bugs, discussions, and feature requests that belong to the Aura work listed above (concurrent gather, XBakes, HG profiles, attachment scripts on HG login/teleport, and the in-progress items in that list) go to **this GitHub repo**:

https://github.com/amandaleeang/OpenSim-Aura/issues


**Everything other feature or bug belongs to OpenSim.** Report bugs to the OpenSim project on [Mantis](http://opensimulator.org/mantis/main_page.php). Use [opensimulator.org](http://opensimulator.org) for documentation, discussion, and new-feature process (including the opensim-dev list).
