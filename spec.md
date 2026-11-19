# eStream Assistant — Windows 11 Application Specification

| | |
|---|---|
| **Status** | Draft v1.0 |
| **Date** | 2026-11-19 |
| **Source** | `Plan.txt` (written on the store PC; kept locally, not in the repo) |
| **Target** | Windows 11 Pro desktop app, runs on the store PC alongside eStream System 5 Office |

---

## 1. Purpose

eStream Assistant is a Windows 11 desktop app that lets the store owner, who is not a programmer, change and look up items in **eStream System 5**  using plain English, barcode scans, and receipt photos. It replaces today's console setup (Claude Code running in `C:\eStreamAutomation`) with a normal windowed app. It keeps the proven automation engine (the PowerShell scripts that drive the eStream UI and are also used by the Power Automate Desktop flow "Estream").

### 1.1 Goals (v1)

- **G1 Chat:** the owner types requests like "Coke 2L to 4.29" or "lays cucumber +10%" and Claude works out the item and the change.
- **G2 Scanning:** a USB barcode scan pulls up the item at once, with no AI call.
- **G3 Receipts:** the owner drags in or pastes a receipt or invoice photo or PDF, then says something like "30% markup on the chips".
- **G4 Safe changes:** every change shows a **Now → New** preview, then a **dry run**, then a **SAVE** button that only a person can press.
- **G5 Stock counts:** counts go through eStream stock sheets, with a guarded post step.
- **G6 History:** the owner can see results of past dry runs and saves.

### 1.2 Non-goals (v1)

- Adding new items, deactivating items, price batches, labels, vendors, purchase orders. Unknown barcodes are added in eStream by hand.
- Automating the eStream POS register (`eStreamPOS.exe`).
- Writing to the eStream SQL database in any way.
- Cloud Power Automate flows. These were tried and did not work with the personal account.
- Scanning while the app is not focused (a global keyboard hook). Planned for later.
- Remote or networked access to the eStream database from other PCs.

---

## 2. Users and environment

### 2.1 User
- **Store owner / manager.** Not technical. Needs big fonts, few buttons, plain-language messages and numbered choices. The app is used at the back-office PC, often with a scanner in hand.

### 2.2 Store PC (production)

| Item | Value |
|---|---|
| Windows user | The normal store account, standard user, **not elevated** |
| OS | Windows 11 Pro |
| Screen | 1920×1080. eStream must be **maximized** |
| .NET | Framework 4.8.1 only, no SDK. The app ships **self-contained** |
| PowerShell | Windows PowerShell 5.1, execution policy *Restricted* |
| Security software | May block PowerShell `Add-Type`. A compiled .NET app is fine |
| eStream | System 5. Office: `C:\Program Files (x86)\eStreamSystem5\Office\eStreamOffice.exe`, window title `eStream System 5 - Office` |
| SQL | `.\SQLESTREAM`, SQL Server 2016 Express, database `eStream`, Windows auth |
| Power Automate Desktop | Store app, flow "Estream" |
| Engine folder | `C:\eStreamAutomation` |

### 2.3 Dev PC
- eStream and its database are **not** present. All development uses mock implementations (§12).

---

## 3. Safety requirements (enforced in code)

These are requirements on the **application code**. The system prompt repeats them, but the app must not depend on the model to follow them.

| ID | Requirement |
|---|---|
| **S1** | **The database is read-only.** The SQL connection uses `ApplicationIntent=ReadOnly`. The repository layer exposes only parameterized `SELECT` statements. No code path builds SQL from strings or runs INSERT, UPDATE, DELETE, MERGE, EXEC or DDL. Preferred: a dedicated `db_datareader` login (needs the owner's approval and SQL admin rights). |
| **S2** | **Dry run before save.** SAVE is enabled only when the current pending change list has just passed a dry run **and has not changed since**. Any edit, add or remove of a row clears the dry-run state. |
| **S3** | **Only a person can save.** No Claude tool can apply changes or post a stock sheet. Apply runs only from the SAVE button, followed by a confirmation dialog that lists every change. |
| **S4** | **Never guess the item.** If a match is ambiguous (below about 85%, or scores are close), the app shows a numbered list and the owner picks one. |
| **S5** | **Stock sheet POST has no undo.** It needs real counts, the sheet number, a dry-run summary, and a typed confirmation (the owner types the sheet number). Test sheets are never posted. |
| **S6** | **Protected objects.** The app refuses to operate on the owner's open stock sheets and on price batches that are pending and not ours. The sheet and batch numbers are kept in local settings on the store PC, not in the repo. |
| **S7** | **Validation limits** mirror `Invoke-EsCsv.ps1`. A row is *Skipped* when: the price or case cost changes by more than 50%, on-hand changes by more than 500 units (unless Force=Y), the deposit or levy code is unknown, the item is inactive, Min > Max, For is not a whole number ≥ 1, or the value is unchanged. Force=Y only after the owner confirms in a dialog. |
| **S8** | **Input safety.** While a dry run or apply is running, the app shows a full-window *"Working — don't touch the mouse or keyboard"* overlay, **does not take focus back** from eStream, and ignores its own input. Foreground and point checks (`Test-EsPoint`) stay inside the scripts. |
| **S9** | **"Allow Deactivations?"** is always answered **No**. The scripts already handle this, and any later C# port must keep it. |
| **S10** | **Secrets.** The API key is stored with DPAPI (`ProtectedData`, CurrentUser) or Windows Credential Manager. It is never written in plain text, to logs, or to the repo. |
| **S11** | **Store data stays private.** Item data, receipts and logs stay on the store PC. They are sent only to the Claude API, as part of requests the owner starts. |

---

## 4. Architecture

### 4.1 Platform
- **C# / .NET 10 LTS, WPF**, published self-contained as a single file for `win-x64`. (Plan.txt said .NET 8/9; both reached end of support in Nov 2026, so .NET 10 LTS is used.)
- Runs **on the store PC**, in the same interactive desktop session as eStream Office.
- Pattern: MVVM. Every external dependency sits behind an interface so it can be mocked.

### 4.2 Component diagram

```
┌──────────────────────────── WPF UI (MVVM) ─────────────────────────────┐
│ ScanBox │ ChatView │ ItemCard │ PendingChangesGrid │ History │ Counts │ Settings │
└────┬─────────┬──────────┬──────────────┬───────────────────────────────┘
     │         │          │              │
ScannerInput  ClaudeService ──tools──► ToolRouter ──► ChangeList (S2 state)
     │         │                         │              │
     ▼         ▼                         ▼              ▼
 IItemRepository ◄───────────────── PricingCalculator  ChangeValidator (S7)
 (SQL / Mock) + NameCache                               │
                                                        ▼
                                              IEStreamExecutor
                                     ┌──────────────┼───────────────┐
                              PowerShellExecutor  PadFlowExecutor  MockExecutor
                              (default, v1)       (optional)       (dev)
                                     │
                                     ▼
                     C:\eStreamAutomation\*.ps1  →  eStream Office UI
```

### 4.3 Components

| Component | Responsibility |
|---|---|
| `IItemRepository` | Read-only lookups: `GetByUpcAsync`, `FindAsync(text, max)`, `GetCodesAsync`, `GetActiveCountAsync`. Implementations: `SqlItemRepository` (Microsoft.Data.SqlClient) and `MockItemRepository`. |
| `NameCache` | Local cache of item names (UPC, Description, Sold, Inactive) in `%LOCALAPPDATA%\eStreamAssistant\` (SQLite or flat file). Refreshes every 12 h, on demand, and once automatically when a search finds nothing. |
| `FuzzyMatcher` | C# port of `Get-EsItem.ps1 -Find` (§9.3). |
| `UpcNormalizer` | Normalization and check-digit fallbacks (§9.1). |
| `PricingCalculator` | Deterministic markup, margin, GM% and price-ending math (§9.2). |
| `ChangeList` | Pending rows plus `DryRunState { None, Running, Passed(hash), Failed }`. SAVE is enabled only when `Passed` and the current hash equals the passed hash. |
| `ChangeValidator` | Rules S7, the same as `Invoke-EsCsv.ps1`. Runs on stage, before dry run, and before apply. |
| `IEStreamExecutor` | `DryRunAsync(rows)`, `ApplyAsync(rows)`, `StockCountPreviewAsync`, `CreateStockSheetAsync`, `PublishStockSheetAsync(sheetId, dryRun)`. |
| `PowerShellExecutor` | Writes a CSV to `Inbox\`, runs the scripts, parses `Done\…_results.csv`, and captures stdout/stderr. |
| `PadFlowExecutor` | Optional. Starts the PAD flow "Estream" (§8.3). |
| `ClaudeService` | Messages API conversation, streaming, tool loop, prompt caching, usage and cost tracking. |
| `ToolRouter` | Checks tool inputs against the schema, then sends them to the repository, ChangeList or executor. Rejects any tool that is not allowed. |
| `ScannerInput` | Detects scanner bursts (§6.2). |
| `ReceiptIngest` | Drag/drop, paste and folder watch. Downscales images, wraps PDFs. |
| `HistoryService` | Reads `Done\*_results.csv` and `Logs\*.log`. |
| `SettingsService` | Settings plus the DPAPI-protected API key. |
| `AppLogger` | App log at `%LOCALAPPDATA%\eStreamAssistant\logs\`. Never logs secrets. Keeps the existing `Logs\` and `Done\` files untouched. |

---

## 5. User interface

### 5.1 Main window

```
+------------------------------------------------------------------+
| [ Scan or search… (focused when idle) ]      ● eStream  ● Claude |
+-----------------------------+------------------------------------+
| CHAT                        | ITEM CARD (last scanned/found)      |
|  owner + Claude messages    |  Description · UPC                  |
|  numbered choices shown as  |  Price / For · Deposit · Levy       |
|  clickable buttons          |  Case cost · Unit cost · GM%        |
|  receipt thumbnails inline  |  On hand · Min / Max · Sold         |
|                             +------------------------------------+
|                             | PENDING CHANGES                     |
|                             |  Item | Field | Now → New | GM% | St|
| [ type here…      ] [Send]  |                                     |
| [ drop receipt here / 📎 ]  |  [ Dry run ]  [ SAVE ]  [ Clear ]   |
+-----------------------------+------------------------------------+
| Chat | History | Stock counts | Settings                          |
+------------------------------------------------------------------+
```

### 5.2 UI requirements
- **U1** Base font is at least 14 pt. Prices and the Now → New column are at least 18 pt. High contrast. Works at 1920×1080 and at 125% scaling.
- **U2** The scan/search box gets focus back whenever the app is idle and focused: after a reply finishes, after a dialog closes, after a grid edit is committed.
- **U3** Status lights: **eStream** (Office process running, window found, maximized) and **Claude** (API key set, last call OK). Each has a tooltip explaining what is wrong.
- **U4** When Claude offers choices (`1.`, `2.`, …), they appear as buttons. Clicking one sends the choice. Typing the number also works.
- **U5** Pending grid columns: Item (description + UPC), Field, Now, New, GM% now → new, Status (Staged / DryRun OK / Skipped: reason / Failed / Applied / Unverified), Reason. Rows can be removed. *New* values can be edited inline, which clears the dry-run state.
- **U6** **SAVE** is disabled unless rule S2 is met. Its tooltip says why ("Run a dry run first", "Changes edited since dry run"). It uses a distinct colour.
- **U7** The SAVE confirmation dialog lists every row as `Description — Field: Now → New`, has the buttons **Save N changes** and **Cancel**, and Cancel is the default.
- **U8** The working overlay (S8) covers the whole window. It shows progress (row x of N, elapsed time) and has a **Stop after current item** button. That button signals the executor between items and never mid-item.
- **U9** Results appear in the grid and as a short summary in chat ("3 applied, 1 skipped: price change over 50%").

---

## 6. Features

### 6.1 Chat
- **F1.1** Free-text requests go to Claude with the tools in §7. Replies stream token by token.
- **F1.2** Each conversation is a **session**. A **New session** button clears the chat. Pending changes stay unless the owner clears them.
- **F1.3** "Ask, don't hunt": at most one search per item mention. If it is ambiguous, show numbered choices (S4).
- **F1.4** Failures (script error, timeout, eStream not running) are explained in plain language with a suggested next step.

### 6.2 Barcode scanning
- **F2.1** Detection: 6–14 digits, each keystroke less than about 40 ms after the last (configurable), ending with Enter. This happens in the scan box or, if focus is elsewhere in the app, through a window-level `PreviewKeyDown` handler.
- **F2.2** A detected scan runs a **direct lookup** with no Claude call and fills the item card.
- **F2.3** Several scans in a row (within a configurable window, default 10 s) build a **scan list** shown in chat as one compact message. The owner can then type one change for all of them ("all to 3.99").
- **F2.4** A scan is added to Claude's context as a short message, `[scanned] 60410079591 → LAYS ALL DRESS 220 g`, so later requests can refer to "this" or "these".
- **F2.5** For an unknown barcode the app says: "Not in eStream. New items must be added in eStream by hand."
- **F2.6** Normalization and fallbacks follow §9.1.

### 6.3 Item card
- **F3.1** Shows the fields from the reference query (§8.1): description, UPC, price, For, case cost, packs/case, units/pack, unit cost, deposit code and amount, levy, on hand, min, max, sold, GM%, inactive flag, last updated.
- **F3.2** Inactive items show a clear **INACTIVE** badge and cannot be staged (S7).
- **F3.3** Any **Note** from UPC normalization is shown, for example "matched without check digit".

### 6.4 Pending changes, dry run and save
- **F4.1** Rows enter the grid from Claude (`stage_changes`) or from inline edits on the item card.
- **F4.2** Every row is validated when staged (S7). Rows that fail stay visible with status *Skipped: reason*.
- **F4.3** **Dry run** writes a changes CSV (format §8.4) and runs `Invoke-EsCsv.ps1 -DryRun -Path <csv>`, then parses the results.
- **F4.4** **SAVE** shows the U7 dialog, then runs `Invoke-EsCsv.ps1 -Path <csv>` with the **same CSV content** that passed the dry run (the hash is checked). The script moves the CSV to `Done\`.
- **F4.5** After apply, rows show *Applied* or *Unverified* (the value read back from the DB did not match). Unverified rows are highlighted and explained.
- **F4.6** **Force=Y** is only possible through a per-row "Allow large change" confirmation dialog.

### 6.5 Receipts and invoices
- **F5.1** Input: drag-drop, Ctrl+V paste, a 📎 file picker, and a watcher on `C:\eStreamAutomation\Receipts\`. Accepted types: JPEG, PNG, WebP, GIF, PDF.
- **F5.2** Images are downscaled to about 1600 px on the long edge before sending. PDFs are sent as document blocks.
- **F5.3** Claude extracts lines (description, vendor code/UPC, qty, case or unit price). It must mark unreadable lines as unreadable and **never invent numbers**.
- **F5.4** Lines are matched to items by UPC first, then by fuzzy match. Ambiguous matches are confirmed with the owner.
- **F5.5** Unit cost = case price ÷ (PacksPerCase × UnitsPerPack). If eStream shows 1/1 for a case item, ask the owner. Deposits and eco fees are **excluded** from cost.
- **F5.6** The app **offers** to update CaseCost and does not assume it.
- **F5.7** Extracted lines appear in a receipt table (line → matched item → cost → proposed price) before anything is staged.
- **F5.8** Handled receipts are moved to `Receipts\Done\` (with a timestamp suffix if the name already exists).

### 6.6 Stock counts
- **F6.1** Input: scan-and-count mode (scan, type count, Enter), CSV import (`UPC,Count`), or counts read from a photo via Claude.
- **F6.2** **Preview** shows over/short against system on-hand. It changes nothing (`Invoke-EsCountCsv.ps1 -Check` / `New-EsStockSheet.ps1 -Check`).
- **F6.3** **Stage 1, create sheet:** an in-app confirm (it uses up a sheet number), then `New-EsStockSheet.ps1 -Path <csv> [-Comment]`. The app records the sheet number. Imports **accumulate**, so the app blocks importing the same counts into the same sheet twice.
- **F6.4** **Stage 2, POST:** only from a human button on the Stock counts tab. It runs `Publish-EsStockSheet.ps1 -SheetId N -DryRun` first, shows the summary, then asks the owner to **type the sheet number** to confirm, then runs without `-DryRun`. Sheets on the S6 protected list cannot be selected.
- **F6.5** The first real POST is marked in the UI as a supervised first-time action (see §15).
- **F6.6** (as built) Only sheets this app made can be posted, each once. The app keeps its own sheet register (`stock-sheets.json`; mock TEST sheets in a separate file). If the script output doesn't include the new sheet number, the owner types it from eStream; a protected or already-listed number is refused.

### 6.7 History
- **F7.1** Lists past runs from `Done\<file>_<time>[_DRYRUN]_results.csv`, newest first, filterable by date, UPC, text and status.
- **F7.2** Opening a run shows its rows (UPC, Description, Changes, Status, Message) and the matching `Logs\<yyyy-MM-dd>.log` excerpt.
- **F7.3** History is read-only.

### 6.8 Settings
- API key (masked, with a test button), model, effort level.
- Engine folder (default `C:\eStreamAutomation`), executor mode (§8.3).
- Default markup %, markup vs margin default, price endings (off / .29 / .49 / .79 / .99, multiple allowed).
- Scanner timing (inter-key ms, min/max digits, scan-list window).
- Name cache: last refresh time and a **Refresh now** button.
- Usage: today's and this month's token counts and estimated cost (from `response.usage`).
- Protected stock sheet and price batch numbers (S6), stored locally.

---

## 7. Claude integration

### 7.1 API usage
- **SDK:** official C# SDK (`dotnet add package Anthropic`). Check type and method names against the SDK README during implementation and do not guess.
- **Model:** setting. Default `claude-opus-5-5`; cheaper option `claude-sonnet-5-5`. Use exact IDs with no date suffix.
- **Thinking/effort:** thinking is always on for Opus 5.5 (do not send "disabled"). Control it with the effort setting. Default **low** for chat and lookups; consider **medium** for receipts (to be tuned in testing).
- **Tool choice:** `auto` with strict tools. Do **not** force a tool choice, because this returns a 400 on Opus 5.5.
- **Refusals and fallbacks:** handle `stop_reason = "refusal"` with a plain message. Turn on server-side fallbacks (beta `server-side-fallback-2026-07-01`, fallbacks `"default"`).
- **Prompt caching:** the stable system prompt and tool definitions come first, marked `cache_control: ephemeral`. Nothing that changes (dates, item data, scans) goes before the breakpoint. Log `usage.cache_read_input_tokens` and alert in the debug log if it is 0 on a warm conversation.
- **Streaming:** required for chat replies.
- **Costs:** compute the cost per request from usage × the price table in settings ($/MTok in, out, and cache read, with model defaults).

### 7.2 System prompt
Based on `C:\eStreamAutomation\CLAUDE.md`, adapted for the app:
- **Keep:** the rules, plain-language style, "ask, don't hunt", barcode handling, receipt rules, markup vs margin, failure handling.
- **Remove:** instructions about running PowerShell, file paths, and slash commands. Tools replace these.
- **Add:** the tool list and the rule "you can stage and dry-run; only the owner can save or post".
- Stored as an embedded resource with a version string, which is logged with each session.

### 7.3 Tools
All tools use JSON Schema with `strict: true` and `additionalProperties: false`. `ToolRouter` checks every input again on the app side.

| Tool | Input | Output | Side effects |
|---|---|---|---|
| `lookup_item` | `upc: string` | Item card fields + normalization note, or `not_found` | Updates the item card |
| `find_items` | `text: string`, `max: int (1–8)` | `[{upc, description, price, match_pct, sold}]` | None |
| `get_codes` | — | Deposit + levy code tables | None |
| `calc_price` | `cost: number`, `mode: "markup"\|"margin"`, `pct: number`, `ending?: string` | `{price, gm_pct, raw_price}` | None (runs in C#) |
| `stage_changes` | `rows: [{upc, price?, for?, case_cost?, deposit?, levy?, on_hand?, min?, max?, force?, reason}]` | Per-row `{staged \| skipped, message}` | Adds or updates grid rows and clears the dry-run state. `force` is ignored unless the owner confirmed it in the UI |
| `run_dry_run` | — | Per-row `{status, message}` | Runs the executor dry run on the current grid (shows the overlay) |
| `stock_count_preview` | `rows: [{upc, count}]`, `add_to_list: bool` | Over/short per row | With `add_to_list`, the counts go into the count list on the Stock counts tab |
| `create_stock_sheet` | `comment: string` | `{sheet, message}` or cancelled | Uses the **count list** (what the owner sees), not rows from Claude. **Blocks on an in-app confirm dialog** |

**Not available as tools, ever:** apply/save, post stock sheet, any SQL write, running arbitrary scripts or shell commands.

### 7.4 Conversation context
- Scans, item card selections, and receipt attachments are added as user-turn content after the cache breakpoint.
- Tool results are short structured JSON. Large result sets are trimmed (top 8 matches).
- After a long session (setting, for example 60 turns), offer a new session to keep costs down.

---

## 8. Integration with eStream

### 8.1 Database (read-only)
Connection string on the store PC:
```
Server=.\SQLESTREAM;Database=eStream;Integrated Security=true;
ApplicationIntent=ReadOnly;TrustServerCertificate=true;Encrypt=false
```
- `BarCode` is a **float**: compare as a number, so leading zeros are lost.
- Reading `Inventory.Description` is slow (about 16 s for any `LIKE` or a full name pull). Only the `NameCache` refresh does a full pull, on a background thread.
- Reference item query (the same as `Get-EsItem.ps1`):
```sql
SELECT CAST(i.BarCode AS decimal(18,0)) UPC, RTRIM(i.Description) Description,
  CAST(i.SellingPrice AS decimal(10,2)) Price, i.PriceMult [For],
  CAST(i.CaseCost AS decimal(10,2)) CaseCost, i.PacksPerCase, i.UnitsPerPack,
  CAST(i.CostPerUnit AS decimal(10,4)) UnitCost, ISNULL(i.DepositLink,0) Deposit,
  ISNULL(d.DollarValue,0) DepositAmt, ISNULL(i.Levy1ID,0) Levy,
  i.Units OnHand, i.MinSOH [Min], i.MaxSOH [Max], i.Sold,
  CAST(i.StoreGM AS decimal(6,2)) GMpct, i.Inactive, i.LastUpdated
FROM Inventory i LEFT JOIN Deposits d ON d.DepositNum = i.DepositLink
WHERE i.BarCode = @upc
```
- Codes come from `Deposits(DepositNum, DepositDesc, DollarValue, LabelDesc)` and `Levies(LevyID, LevyDesc, LevyPrice)`.
- Tables that must never be changed (and cannot be, because the DB is read-only): `Products` (POS copy synced by the server), `PBInfo`/`PBDetail`, `StockSheets`, `Adjustments`.

### 8.2 PowerShell engine (default executor)
Every call uses this form:
```
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\eStreamAutomation\<script> <args>
```
- Runs in the interactive user session, not elevated, with no visible console window (`CreateNoWindow`). stdout and stderr are captured.
- Per-call timeout: about 60 s base plus about 30 s per item (configurable).
- Only **one** executor operation runs at a time (global lock), because UI automation cannot run in parallel.

| App operation | Script |
|---|---|
| Lookup (fallback / v1 if SQL repo not ready) | `Get-EsItem.ps1 <UPC>` / `-Find "text" [-Refresh] [-Top n]` / `-Codes` |
| Dry run | `Invoke-EsCsv.ps1 -DryRun -Path <csv>` |
| Apply | `Invoke-EsCsv.ps1 -Path <csv>` |
| Count preview | `Invoke-EsCountCsv.ps1 -Check` or `New-EsStockSheet.ps1 -Path <csv> -Check` |
| Create stock sheet | `New-EsStockSheet.ps1 -Path <csv> [-Comment x]` |
| Post stock sheet | `Publish-EsStockSheet.ps1 -SheetId N [-DryRun]` |

Ignore the old wrappers (`*.old`, `*.bak`, `Set-EsPrice.ps1`, `Invoke-EsPriceCsv.ps1`).

### 8.3 Power Automate Desktop
- The flow **"Estream"** (menu: 1 DRY RUN, 2 APPLY, 3 Look up, 4 Codes, 5 Open Inbox, 6 Count preview, 7 Make sheet, 8 POST) stays as a **manual fallback tool**. The app must not break it: it uses the same `Inbox\`, `Done\` and `Logs\` folders and file formats.
- **Default executor: direct scripts.** PAD's flow only wraps the same scripts and shows results in dialog boxes, which an app cannot read back reliably.
- **Optional `PadFlowExecutor`** (setting "Run through Power Automate Desktop"): starts the flow through its run URL (`ms-powerautomate:/console/flow/run?...` with the flow and environment IDs). This needs a non-interactive variant of the flow that takes the operation and CSV path as input and writes to `Done\`, so the app can parse the same results CSV. **The run URL must be checked on the store PC before relying on it.**
- The app has a **"Open PAD flow"** button in Settings for manual use.

### 8.4 File formats
- **Changes CSV** (`TEMPLATE-changes.csv`): `UPC,Price,For,CaseCost,Deposit,Levy,OnHand,Min,Max,Force`. Empty cell = no change. UTF-8 (no BOM), CRLF line endings, named `assistant_<yyyyMMdd-HHmmss>.csv`.
  - **Dry runs** write it to the app's own `%LOCALAPPDATA%\eStreamAssistant\runs\` and pass it with `-Path`, **never to `Inbox\`**. A CSV left in `Inbox\` would be applied by anyone choosing APPLY in the PAD flow, which would bypass SAVE (S2/S3).
  - **Apply** (SAVE, Phase 4) writes the same CSV content (hash checked) to `Inbox\` and runs it, and the script moves it to `Done\`.
- **Counts CSV** (`TEMPLATE-counts.csv`): `UPC,Count`.
- **Results CSV:** `UPC, Description, Changes, Status, Message`. Status values include DryRun, Done/Applied, Skipped (with reason), Failed, Unverified. **Read the exact strings from `Invoke-EsCsv.ps1` and the sample files in `Done\` before writing the parser.** Unknown statuses are shown as-is and treated as *not successful*.
- The stock-sheet import file (`C:\ProgramData\estream\Import\CMStock<sheetID>.dat`) is written **only by the scripts**, never by the app.

---

## 9. Business logic

### 9.1 UPC normalization
1. Strip non-digits, then strip leading zeros.
2. Lookup order: exact → without the last digit → with a computed check digit appended.
3. Check digit (UPC/EAN mod-10): working from the right, weights 3,1,3,1…; check = (10 − sum mod 10) mod 10.
4. Report which fallback matched (the Note).
5. Must give the same results as the logic in `Get-EsItem.ps1` (unit tests use cases taken from it).

### 9.2 Pricing math (C#, `decimal`)
- Markup X%: `price = cost × (1 + X/100)`. Example: 30% on $4.29 = **$5.58**.
- Margin X%: `price = cost / (1 − X/100)`. Example: 30% on $4.29 = **$6.13**. X must be < 100.
- `GM% = (price − cost) / price × 100`.
- Round to the cent (MidpointRounding.AwayFromZero), then optionally move **up** to the nearest allowed ending (.29/.49/.79/.99, from settings).
- Cost basis is the unit cost, excluding deposit and levy.
- If the request does not make clear whether it means markup or margin, show both prices and ask.
- "+10%" on a price means `price × 1.10`, then the ending rule applies.
- Multi-pack ("2 for $5"): Price = 5.00, For = 2.

### 9.3 Fuzzy search (port of `Get-EsItem.ps1 -Find`)
1. Upper-case the query, split it into words, and drop stop-words (the, and, chips, bag, pop, …). Copy the exact list from the script.
2. Candidates: description contains a query word, or the word's first 3 or last 3 letters.
3. Word score against the best-matching description word: exact 1.0; prefix 0.85; contains 0.75; otherwise `1 − Levenshtein/maxLen` (only if the lengths differ by ≤ 2).
4. Item score = average over query words. Keep items scoring ≥ 0.5. Sort by score, then by Sold (descending). Return the top 8.
5. If the best score is below 0.85, or the top two are close (within 0.10, tunable), ask the owner (S4).
6. A search that finds nothing triggers one automatic cache refresh and retry.

### 9.4 Validation (ChangeValidator)
Uses the S7 rules on the current item values from the repository. The engine **checks again itself**. If the app and engine disagree, the engine's result is what counts and is shown.

---

## 10. Error handling

| Situation | Behaviour |
|---|---|
| eStream Office not running | Red light; dry run and save disabled with the tooltip "Open eStream Office". |
| eStream not maximized or covered | The scripts maximize it or stop. Show the script message in plain language. |
| Script timeout | Kill the process tree, mark the rows *Failed: timeout*, write to the log, and advise checking the eStream screen. |
| Script non-zero exit / no results CSV | Show stderr in the debug panel; rows become *Failed*. The dry-run state is never marked as passed. |
| SQL unavailable | Fall back to `Get-EsItem.ps1` for lookups; amber light. |
| Claude API error / no key / offline | Scanning, lookup, the grid, dry run and save all still work without Claude. Chat shows the error and a retry option. |
| Claude refusal | Show a neutral message; the conversation can continue. |
| App crash during apply | On restart, check `Inbox\` and `Done\` for an unfinished run and show its results in History. Never re-apply automatically. |

---

## 11. Non-functional requirements

- **Performance:** scan to item card in ≤ 1 s (SQL repo). Fuzzy find in ≤ 300 ms from the in-memory cache. UI never freezes (all I/O is async).
- **Reliability:** single-instance app (a named mutex). The executor lock prevents runs from overlapping.
- **Security:** S1, S10 and S11. The app does not need admin rights. No network listeners.
- **Logging:** app log rotated daily and kept 90 days. Every executor call is logged with its arguments, exit code and duration. Tool calls are logged with inputs and results; message text is logged only when debug logging is on.
- **Accessibility:** works with keyboard only; scales to 150% without clipping.
- **Offline:** everything except chat and receipt reading works without internet.

---

## 12. Development and testing

### 12.1 Dev setup
- Copy the whole `C:\eStreamAutomation` folder (scripts, `eStream-map.md`, `CLAUDE.md`, templates, sample `Done\*_results.csv`) and `Plan.txt` to the dev PC.
- Use `MockItemRepository` (sample items plus deposit and levy tables) and `MockEStreamExecutor` (simulated delays and Skipped, Failed and Unverified results).
- Optional: a read-only sample export from the store PC (`sqlcmd … TOP 2000 … ORDER BY Sold DESC`, plus Deposits and Levies). **This is store data: keep it out of the repo** (add to `.gitignore`).
- **Never** connect to the eStream DB over the network.

### 12.2 Unit tests (xUnit)
- UPC normalization and check digits (including cases with leading zeros, a missing check digit, and an extra digit).
- Fuzzy scoring against known abbreviations ("CUCMBER LAYS", "LAY'S CUCUMBER").
- Pricing math, including the §9.2 examples and the ending rules.
- CSV writing and results parsing, against real sample files.
- ChangeValidator: one test per S7 rule.
- ChangeList: dry-run state is cleared by any change; SAVE is only enabled when the hash matches.
- ToolRouter: rejects unknown tools, rejects bad input, cannot apply or post.
- Denylist (S6) checks.

### 12.3 Integration / regression
- Recorded Claude conversations replayed against mocks: an ambiguous name, a receipt photo, a scan list, a markup vs margin question, an attempt to save through chat (must be refused).
- Store-PC acceptance (Phase 7) with real SQL and the real PowerShell executor.

---

## 13. Build phases and acceptance criteria

| Phase | Scope | Done when |
|---|---|---|
| **1** | Shell, settings (DPAPI key), mock repo, UPC + fuzzy lookup, scan detection, item card | Scans and searches fill the item card from mock data; unit tests for UPC and fuzzy pass |
| **2** | Claude chat, read-only tools, streaming, caching, numbered-choice buttons | "lays cucumber" gives numbered choices; cache reads > 0 on the second turn |
| **3** | Pending grid, `stage_changes`, validator, pricing, dry run (mock, then PowerShell) | Dry run results shown for each row; any edit turns SAVE off |
| **4** | SAVE + confirmation → apply → verified results; History tab | Mock apply shows Applied and Unverified; History lists Done\ runs |
| **5** | Receipts: drag/drop, paste, folder watch, receipt table | A sample receipt gives correct unit costs; unreadable lines are flagged; file moved to Receipts\Done |
| **6** | Stock counts: preview, create sheet, guarded post | Post needs the typed sheet number; denylisted sheets cannot be chosen |
| **7** | Store-PC acceptance | Dry runs on 60410079591 (LAYS ALL DRESS 220 g) and 60410079966 (CUCMBER LAYS) pass; **one** real save with the owner watching is Applied and verified |
| Later | C# SQL lookups by default, optional C# UI automation (must keep every check in §15), global scan hook, new-item creation | — |

---

## 14. Deployment

- `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`
- Install to `C:\eStreamAssistantApp\`. Settings and logs go in `%LOCALAPPDATA%\eStreamAssistant\`.
- Runs as the normal store user, not elevated.
- Start-menu shortcut. The owner pins it to the taskbar by hand.
- SmartScreen or antivirus may flag an unsigned EXE: code-sign it, or add an exclusion with the owner's approval.
- The existing console assistant (desktop shortcut, `CLAUDE.md`, `.claude\`) and the PAD flow keep working alongside the app.
- eStream Office must be running. The scripts maximize it.

---

## 15. Constraints carried from field experience

Any future port of the automation **must keep** these:
- Auto-maximize eStream. Compare the Item List count with the DB active count and reload if they differ. Use only the real search combo. Double-click the yellow row. Run `Test-EsPoint` before every click.
- The editor-header UPC check is the last line of defence and is never removed.
- Item List search is a FIND (it scrolls and highlights), not a filter. The list starts empty until List Setup > Show All Active Items.
- Exit in the item editor throws away changes with no prompt (this is how dry runs work).
- New stock sheets pre-fill Counted with system on-hand (negatives become 0). Cut Off defaults to yesterday. Tabbing into Supervisor opens a picker.
- A real stock-sheet POST has **never** been done. The first one needs the owner's real counts and go-ahead.
- Do not bulk-edit the `.ps1` scripts with string replaces. Edit carefully and re-parse.

---

## 16. Open questions

1. **Power Automate role:** is direct script execution (recommended) acceptable, or must every eStream operation go through the PAD flow? If PAD is required, the flow needs a non-interactive variant and the run URL must be checked on the store PC.
2. **Read-only SQL login:** will the owner approve creating a `db_datareader` login?
3. **Anthropic Console account:** does the owner have one, with billing set up and an API key? (API use is billed separately from any Claude subscription.)
4. **Default model and effort:** Opus 5.5 or Sonnet 5.5 for daily use?
5. **Price endings:** which endings, and should they apply by default?
6. **Code signing:** buy a certificate, or use an antivirus exclusion?
7. **UI framework:** WPF (recommended) or WinUI 3?
