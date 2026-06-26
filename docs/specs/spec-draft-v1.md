\# Functional \& Technical Specification: Cross-Platform File Pipeline Utility



\---



\## 1. Product Overview \*(Updated)\*



The File Pipeline Utility is a lightweight, rule-based file synchronization, aggregation, and automation engine designed for Windows and Linux. It operates as a background service that sits in the system task tray, offering users a graphical user interface (GUI) for configuration and previewing, alongside operating system context-menu integration.



The core purpose of this tool is to automate complex, multi-point file-routing workflows (e.g., distributing built assets, aggregating modular project components, mirroring workspaces, or archiving directories) while introducing strict verification layers to eliminate accidental file loss.



\---



\## 2. System Architecture \& Topology Support



To support advanced folder relations, the engine decouples the source ingestion layer from the target distribution layer. This allows it to handle complex execution Topologies via a unified processing queue.



```

Many-to-One (Aggregation)             Many-to-Many (Consolidated Mirroring)



&#x20;Source A ──┐                          Source A ──┐      ┌──► Target X

&#x20;           │                                     ├─────►┤

&#x20;Source B ──┼─► Target X               Source B ──┼─►    └──► Target Y

&#x20;           │                                     │

&#x20;Source C ──┘                          Source C ──┘



```



The system is split into three decoupled subsystems to ensure platform agility and execution stability:



1\. \*\*The Core Engine (Headless Background Service):\*\* Manages file system watching across multiple arbitrary roots, coordinates concurrent file transfer streams, executes post-transfer verification hashes, and processes task queues.

2\. \*\*The Shell Integration Layer:\*\* Platform-specific hooks that pass folder or file target payloads to the execution engine via command-line arguments or IPC.

3\. \*\*The Configuration GUI:\*\* An administrative application used strictly to mutate configuration schemas, monitor system logs, and view execution dry runs.



\---



\## 3. Core Functional Requirements



\### 3.1 Advanced Topology Configurations \*(Updated)\*



The execution engine must map relationship schemas across four distinct directory topologies:



\* \*\*One-to-One (1:1):\*\* Classic mirroring or moving from a single source to a single destination.

\* \*\*One-to-Many (1:$N$ - Distribution):\*\* A single absolute source directory distributes its payload simultaneously to $N$ independent target directories.

\* \*\*Many-to-One ($M$:1 - Aggregation):\*\* Flattens and combines the contents of $M$ independent source directories into a single target directory.

\* \*Constraint:\* Collisions between identical filenames originating from different source folders must be handled explicitly by the active profile's conflict resolution policy.





\* \*\*Many-to-Many ($M$:$N$ - Consolidated Mirroring):\*\* Aggregates the contents of $M$ independent source directories and mirrors that unified, consolidated state across $N$ target directories.



\### 3.2 Automated Triggers



The execution engine must respond to three distinct trigger methodologies:



\* \*\*Manual Shell Invocation:\*\* Triggered explicitly via the operating system's right-click context menu on a selected file or folder. When executed on an $M$:X profile, it evaluates all configured sources.

\* \*\*Reactive File Watcher:\*\* Continuous automated background monitoring of all configured source directories in a profile.

\* \*Constraint:\* The watcher must implement a configurable settling delay (debouncing) per source to prevent processing files that are actively being written to by another process.





\* \*\*Scheduled / Interval:\*\* Automated execution based on explicit time loops or system state transitions.



\### 3.3 Transactional Verification \& Integrity Safeguards



To guarantee data safety prior to executing destructive "Delete Source" triggers:



\* \*\*Payload Verification:\*\* The engine must cross-examine files post-transfer using a user-defined verification tier:

\* \*\*Tier 1 (Lightweight):\*\* Comparative size and last-modified timestamp validation.

\* \*\*Tier 2 (Cryptographic):\*\* Byte-stream checksum generation (e.g., SHA-256 validation across all validated Sources and Targets).





\* \*\*Rollback Protocol:\*\* If any target copy or hash verification fails mid-pipeline, the operation must abort, flag an error, and execute a cleanup routine to remove orphaned or partially copied files from the targets, preserving all source roots intact.

\* \*\*Atomic Sourced Deletion:\*\* For "On Success, Delete Source" triggers in an $M$:X configuration, the engine must verify that a specific source file was successfully copied to \*all\* destinations before removing it from its specific source root, preventing a single failing target drive from halting safe cleanup on other drives.



\### 3.4 Conflict Resolution Strategies \*(Updated)\*



When a filename collision occurs at a target destination (either against an existing target file or during an $M$:X source aggregation step), the engine must execute a deterministic rule defined by the active profile:



| Strategy | Action |

| --- | --- |

| `Overwrite If Newer` | Replaces the target file only if the incoming file has a more recent modification timestamp. |

| `Rename Suffix` | Preserves both files by appending a standardized timestamp or source identifier pattern to the incoming file (e.g., `filename\_YYYYMMDD\_SourceA.ext`). |

| `Source Priority / Override` | Resolves aggregation collisions by strictly prioritizing files from specific source paths based on user-defined weighting (e.g., Source A always wins over Source B). |

| `Mirror / Purge` | Enforces exact parity. If a file does not exist in \*any\* of the configured sources, it is pruned from the targets. |

| `Accumulate` | Copies new items but ignores deletions or modifications on existing target files. |



\### 3.5 Filtering and Rule Matching



Profiles must support conditional parsing logic to restrict execution payloads:



\* \*\*Inclusion/Exclusion Lists:\*\* Wildcard pattern matching (e.g., `\*.tmp`, `/build/\*`) to filter out operational junk data or target specific extensions.

\* \*\*Path-Specific Filters:\*\* In $M$:X configurations, allows unique exclusion rules to apply to specific source directories before they are merged into the target pipeline.

\* \*\*Threshold Barriers:\*\* Boundaries governing acceptable execution states, such as maximum/minimum file size or modification age metrics.



\---



\## 4. Proposed Feature Extensions



To maximize the utility of advanced $M$:$N$ mappings, the following capabilities should be supported natively by the engine core:



\### 4.1 Virtual Namespace Mapping (Virtual Folders)



Instead of flatly merging the contents of $M$ directories into a single target folder and risking thousands of naming collisions, allow the profile configuration to define a custom target subdirectory token for each source.



\* \*Example:\* If Source A is `C:\\Projects\\Assets` and Source B is `D:\\Audio\\SFX`, the target directory can map them into `\[Target]\\Assets\\` and `\[Target]\\SFX\\` automatically within a single profile, preserving structure seamlessly.



\### 4.2 Sparse Syncing (Metadata-Only Sync)



For heavy directory structures or slow network targets, allow a "Placeholder" or "Log-Only" sync mode. It copies the directory architecture and logs changes without pushing full payloads until explicitly triggered or requested, optimizing local network bandwidth.



\---



\## 5. Operational \& User Interface Requirements



\### 5.1 System Tray Infrastructure



\* The application must minimize to the native operating system's task tray/notification area upon launch.

\* Right-clicking the tray icon must surface a native context menu allowing users to quickly toggle active file watchers, view engine status, open the Configuration GUI, or terminate the application.



\### 5.2 Configuration GUI \& Execution Previewer



\* \*\*Visual Profile Manager:\*\* A graph-like or multi-list interface to visually link multiple source blocks to multiple target blocks.

\* \*\*Simulation Mode ("Dry Run"):\*\* A functional evaluation sandbox. When invoked, the engine processes the selected pipeline and surfaces an execution plan in the GUI showing exactly which files will be copied, aggregated, renamed, or deleted across the topology without modifying any storage media.

\* \*\*Live Telemetry:\*\* Real-time progress bars, throughput speeds, and log outputs showing active pipelines sorted by active source root execution.



\---



\## 6. Technical Specification \& Agnostic Architecture



\### 6.1 Declarative Configuration Schema (JSON) \*(Updated)\*



The behavior of every synchronization pipeline must be completely defined via standard, serializeable JSON files. This accommodates arrays of objects for both sources and targets.



\#### System Profile Configuration Blueprint



```json

{

&#x20; "$schema": "http://json-schema.org/draft-07/schema#",

&#x20; "ProfileId": "00000000-0000-0000-0000-000000000000",

&#x20; "Name": "Agnostic Many-to-Many Profile",

&#x20; "Active": true,

&#x20; "Sources": \[

&#x20;   {

&#x20;     "Path": "/absolute/path/to/source/alpha",

&#x20;     "MonitorChanges": true,

&#x20;     "SettleDelaySeconds": 2,

&#x20;     "VirtualSubFolder": "Alpha\_Component"

&#x20;   },

&#x20;   {

&#x20;     "Path": "/absolute/path/to/source/beta",

&#x20;     "MonitorChanges": true,

&#x20;     "SettleDelaySeconds": 5,

&#x20;     "VirtualSubFolder": "Beta\_Component"

&#x20;   }

&#x20; ],

&#x20; "Targets": \[

&#x20;   { "Path": "/absolute/path/to/target/primary" },

&#x20;   { "Path": "/absolute/path/to/target/mirror\_nas" }

&#x20; ],

&#x20; "Policies": {

&#x20;   "ConflictResolution": "SourcePriorityOverride",

&#x20;   "SourcePriorityOrder": \["/absolute/path/to/source/alpha", "/absolute/path/to/source/beta"],

&#x20;   "VerificationMethod": "SHA256",

&#x20;   "OnSuccess": "MoveToRecycleBin",

&#x20;   "OnFailure": "AbortAndCleanTargets"

&#x20; },

&#x20; "Filters": {

&#x20;   "Include": \["\*.json", "\*.bin", "\*.png"],

&#x20;   "Exclude": \[".DS\_Store", "Thumbs.db", "\*\*/tmp/\*"]

&#x20; }

}



```



\### 6.2 Operating System Integration Constraints



\#### Windows Execution Target



\* \*\*Context Menu:\*\* Implemented via Shell command verb registrations within the Windows Registry under file and directory structures pointing to the background pipeline host.

\* \*\*File Operations:\*\* Destructive actions designated as `MoveToRecycleBin` must map cleanly to the shell file operation APIs (`IFileOperation`) to guarantee visibility within the system trash repository.



\#### Linux Execution Target



\* \*\*Context Menu:\*\* Implemented using `.desktop` action files compliant with FreeDesktop specifications, allowing integration into standard file managers (e.g., Nautilus, Dolphin).

\* \*\*File Operations:\*\* Deletions requiring safe retention must conform to the FreeDesktop Trash Specification, routing dropped payloads directly to `\~/.local/share/Trash/`.



\### 6.3 Concurrency and File Handling



\* \*\*Isolating Mutexes:\*\* The pipeline engine must maintain strict file-level read/write locks when verifying target streams to prevent file mutation during a hash calculation phase.

\* \*\*Sequential Job Queueing:\*\* If a file watcher detects rapid modifications across multiple source paths while a large transfer is currently underway, incoming requests must append to an asynchronous, sequential job queue rather than spawning competing I/O operations on the same disk sectors.

