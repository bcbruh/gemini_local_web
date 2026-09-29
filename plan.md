# Kế hoạch xây dựng Gemini Local Agent

> Tài liệu nguồn về mục tiêu và yêu cầu sản phẩm: [`decribe.md`](./decribe.md).  
> Tài liệu này chuyển các yêu cầu đó thành kiến trúc, thứ tự triển khai, đầu ra và tiêu chí nghiệm thu.

## 1. Cách dùng tài liệu này

Kế hoạch được tổ chức theo các **cổng chất lượng** thay vì chỉ theo danh sách tính năng. Một giai đoạn chỉ được xem là hoàn thành khi thỏa tiêu chí nghiệm thu; tính năng của giai đoạn sau không được làm vòng qua lớp bảo mật hoặc kiến trúc đã định ở giai đoạn trước.

Ba mốc sản phẩm chính:

- **Technical Proof**: chứng minh có thể đăng nhập và trao đổi ổn định với Gemini Web qua một adapter cô lập.
- **MVP**: hoàn thành vòng lặp đọc/search → suy luận → trả lời và patch → review → apply trong một workspace.
- **V1**: đủ an toàn và ổn định để dùng hằng ngày cho các tác vụ coding thông thường, bao gồm chạy test/command, phục hồi session và đóng gói `.exe`.

Mỗi khi thay đổi quyết định kiến trúc quan trọng, tạo một ADR trong `docs/adr/` thay vì sửa code âm thầm theo một hướng mới.

## 2. Phạm vi sản phẩm

### 2.1 Kết quả cần đạt

Người dùng Windows có thể:

1. Tải và mở một executable.
2. Kết nối tài khoản Gemini bằng browser flow, không nhập API key hoặc cookie thủ công.
3. Chọn một workspace.
4. Chat để agent đọc, tìm kiếm, sửa code và chạy các lệnh development được kiểm soát.
5. Xem trạng thái, command và diff; phê duyệt những hành động cần xác nhận.
6. Đóng và mở lại ứng dụng mà vẫn khôi phục được workspace cùng conversation chính.

### 2.2 Phạm vi MVP

- Một Gemini provider: Gemini Web.
- Một workspace tại một thời điểm.
- Một conversation chính.
- Tool: liệt kê thư mục, đọc file, search tên/nội dung, chuẩn bị patch, apply patch có approval.
- Stream trạng thái hoạt động lên local web.
- Workspace sandbox, path validation, secret redaction cơ bản.
- Lịch sử local tối thiểu và khả năng cancel run.

### 2.3 Phạm vi V1

MVP cộng thêm:

- Chạy test/command với policy, timeout, output limit và hủy process tree.
- Ba permission mode: `ask_before_changes`, `auto_edit_workspace`, `read_only`.
- Session restore, reconnect Gemini, crash recovery.
- Context compaction và kiểm tra file version trước khi sửa.
- Git status/diff ở mức chỉ đọc.
- Portable Windows `.exe`, diagnostic log đã redact, quy trình release có chữ ký.

### 2.4 Chưa làm trước V1

- Multi-agent, multi-provider, nhiều conversation.
- Cloud sync/history/account.
- MCP, browser computer-use tổng quát, deploy, git push.
- Editor extension, tray app, context-menu integration.
- Auto-update binary không có signature verification.
- Index toàn bộ project ngay khi chọn workspace.

## 3. Chỉ số thành công và giới hạn vận hành

Các ngưỡng cụ thể có thể điều chỉnh sau benchmark, nhưng phải có test đo được:

| Nhóm | Mục tiêu ban đầu |
|---|---|
| Onboarding | Người dùng mới đi từ mở app đến gửi prompt đầu tiên mà không dùng terminal hoặc chỉnh config |
| Startup | UI local xuất hiện trong khoảng 3 giây trên máy phát triển chuẩn, không tính browser login lần đầu |
| Workspace safety | 100% test traversal, symlink/junction escape và path ngoài workspace bị chặn |
| Patch safety | Không ghi đè nếu hash file khác snapshot đã đọc |
| Command safety | Mọi command có policy decision, timeout và audit event |
| Recovery | Sau restart, message đã commit và action đã xác nhận được phục hồi; action dang dở được đánh dấu `interrupted` |
| Privacy | Credential không nằm trong frontend, SQLite history, log hoặc crash dump do ứng dụng chủ động tạo |
| Adapter isolation | Có thể chạy toàn bộ agent test bằng Fake Brain mà không cần Gemini/Web/network |
| Reliability | Malformed model output không tự biến thành tool action; parser fail closed |

## 4. Quyết định kiến trúc nền tảng

### 4.1 Kiểu kiến trúc

Dùng **modular monolith** cho V1: một process local, nhiều module có ranh giới rõ, một cơ sở dữ liệu local. Cách này giữ packaging và recovery đơn giản nhưng vẫn cho phép thay Gemini adapter hoặc frontend sau này.

Không tách microservice. Không để frontend gọi trực tiếp filesystem, shell hoặc Gemini.

```text
System Browser
    │  authenticated localhost HTTP + event stream
    ▼
Local App Host
    ├── Local Web API / UI assets
    ├── Agent Orchestrator ─── Brain Port ─── Gemini Web Adapter
    ├── Tool Registry
    │     ├── Workspace read/list/search
    │     ├── Patch/diff/apply
    │     ├── Command runner
    │     └── Git read-only
    ├── Policy + Approval Engine
    ├── Context Builder + Secret Redactor
    ├── History / Event Store
    └── Credential Store (Windows-protected)
```

### 4.2 Stack đề xuất

Đây là baseline để triển khai nhất quán; chốt bằng ADR trước khi viết production code:

- **Host/backend**: C# trên .NET LTS, ASP.NET Core/Kestrel, async/cancellation xuyên suốt.
- **Frontend**: React + TypeScript + Vite; build thành static assets và nhúng vào executable.
- **Persistence**: SQLite với migration có version; single writer do backend quản lý.
- **Credential**: Windows DPAPI/Credential Manager qua một `ICredentialVault`; tuyệt đối không nằm trong SQLite.
- **Realtime UI**: Server-Sent Events cho run events ở V1; command từ UI dùng HTTP. Chỉ chuyển sang WebSocket khi thực sự cần giao tiếp hai chiều liên tục.
- **Validation**: JSON Schema cho model protocol và API contract.
- **Test**: xUnit cho backend, Vitest/Testing Library cho frontend, Playwright chỉ dùng trong development/CI cho E2E UI.
- **Packaging**: self-contained single-file Windows publish; không bundle Chromium chỉ để hiển thị UI.

Lý do chọn .NET: phù hợp Windows single executable, process/cancellation tốt, API local gọn và tích hợp cơ chế bảo vệ credential của Windows thuận lợi. Nếu spike Gemini chứng minh adapter browser khó triển khai trên .NET, chỉ adapter được phép dùng sidecar; agent core không đổi.

### 4.3 Các nguyên tắc không được phá vỡ

1. Agent core chỉ biết `IBrain`; không biết cookie, endpoint hoặc HTML của Gemini.
2. Model chỉ **đề nghị** action; runtime mới là nơi validate, authorize và thực thi.
3. Local runtime là source of truth cho file/tool state. Model conversation không phải audit log.
4. Tool input phải typed và validated; không suy đoán action từ prose bị lỗi.
5. Mọi path phải canonicalize và kiểm tra workspace boundary tại thời điểm sử dụng.
6. File edit dựa trên snapshot/hash; mismatch phải đọc lại, không force overwrite.
7. Mọi output trước khi vào log, history hoặc model context đều đi qua size limit và secret redaction.
8. UI không bao giờ nhận raw Gemini credential.

## 5. Ranh giới module

### 5.1 `AppHost`

- Chọn random free port và bind duy nhất vào `127.0.0.1`/`::1` theo cấu hình an toàn.
- Tạo local access session mới mỗi lần chạy.
- Chạy migration, khởi tạo module, mở system browser và shutdown sạch.
- Bảo đảm chỉ một instance hoặc chuyển focus/mở URL của instance đang chạy.

### 5.2 `LocalWeb`

- Phục vụ frontend assets và typed API.
- Xác thực local browser session, kiểm tra Origin/Host và CSRF.
- Cung cấp snapshot state và stream event có sequence number để reconnect.
- Không chứa business logic cho tool hay Gemini.

### 5.3 `Agent.Core`

- State machine của một run.
- Gọi `IBrain`, parse protocol, dispatch tool, chờ approval và gửi observation lại.
- Giới hạn số vòng, tổng thời gian, kích thước context và số lần retry.
- Cancel run qua `CancellationToken` và ghi lại trạng thái cuối.

### 5.4 `Brain.Abstractions`

Contract tối thiểu:

```text
Connect / Disconnect / GetStatus
StartOrContinue(request, cancellation) -> BrainResponse
ResetRemoteConversation (nếu adapter hỗ trợ)
```

`BrainResponse` chỉ chứa response chuẩn hóa: final message, structured action request, usage/diagnostic metadata không nhạy cảm, hoặc typed error.

### 5.5 `Brain.GeminiWeb`

- Quản lý login/browser profile hoặc protected session.
- Chuyển request chuẩn của core sang request Gemini Web.
- Chuẩn hóa response/error; phát hiện session expired và compatibility break.
- Không đọc workspace, chạy command hoặc tự apply patch.
- Có fixture/contract test để phát hiện response format thay đổi.

### 5.6 `Workspace`

- Quản lý workspace hiện tại và canonical root.
- List/read/search với ignore rules, file size/type limits.
- Tạo snapshot gồm path, size, modified time và content hash.
- Không trả raw secret nếu policy không cho phép.

### 5.7 `Tools`

Mỗi tool có:

- Tên và protocol version.
- Input/output schema.
- Risk classification.
- Hàm `validate`, `authorize`, `execute`.
- Timeout/output budget/idempotency rule.
- Event/audit representation an toàn.

### 5.8 `Policy`

- Quyết định `allow`, `require_approval` hoặc `deny` dựa trên permission mode, tool, input và workspace state.
- Chính sách cứng luôn thắng setting UI.
- Approval gắn với hash chính xác của action; sửa payload làm approval cũ mất hiệu lực.

### 5.9 `Context`

- Chọn message/tool result cần gửi.
- Đóng gói code/file content như untrusted data.
- Redact secret, truncate output, loại snapshot cũ.
- Compact lịch sử thành summary có nguồn gốc nhưng không thay thế audit store.

### 5.10 `Persistence`

- Transaction cho conversation, run, event, tool call và approval.
- Migration tiến, có backup trước migration phá vỡ tương thích.
- Không lưu credential hoặc full output không giới hạn.

## 6. Cấu trúc repository dự kiến

```text
/
├── decribe.md
├── plan.md
├── docs/
│   ├── adr/
│   ├── threat-model.md
│   ├── protocol-v1.md
│   └── release-checklist.md
├── src/
│   ├── AppHost/
│   ├── LocalWeb/
│   ├── Agent.Core/
│   ├── Brain.Abstractions/
│   ├── Brain.GeminiWeb/
│   ├── Tools/
│   ├── Workspace/
│   ├── Policy/
│   ├── Context/
│   ├── Persistence/
│   ├── Security/
│   └── Frontend/
├── tests/
│   ├── Unit/
│   ├── Integration/
│   ├── Contract/
│   ├── Security/
│   └── E2E/
├── fixtures/
│   ├── workspaces/
│   └── gemini-responses/
└── build/
```

Các project/module chỉ phụ thuộc hướng vào abstraction; `Brain.GeminiWeb` không được tham chiếu từ `Tools`, `Workspace` hoặc `Policy`.

## 7. State machine và dữ liệu

### 7.1 Run state

```text
queued
  -> preparing_context
  -> waiting_for_brain
  -> validating_action
  -> waiting_for_approval  ── reject ──> waiting_for_brain
  -> running_tool
  -> waiting_for_brain
  -> completed | failed | cancelled | interrupted
```

Quy tắc:

- Chỉ một active run trong conversation V1.
- Mỗi transition được ghi thành event có `sequence`, `timestamp`, `run_id`.
- Restart biến trạng thái không kết thúc thành `interrupted`; không tự động chạy lại command/edit.
- Retry request mạng chỉ được tự động khi không thể gây lặp tool side effect.

### 7.2 Tool-call state

`proposed → validated → authorized/waiting_approval → executing → succeeded/failed/rejected/cancelled`

Mỗi tool call có `tool_call_id` và idempotency key. Observation trả về model phải tham chiếu đúng ID.

### 7.3 Data model tối thiểu

| Entity | Nội dung chính |
|---|---|
| `app_state` | schema version, workspace gần nhất, permission mode |
| `conversations` | conversation chính, local/remote reference nếu có |
| `messages` | role, content đã sanitize, created time |
| `runs` | status, timestamps, error category, cancel state |
| `run_events` | sequence, public event type, payload đã redact |
| `tool_calls` | tool/version, validated input, status, bounded result |
| `approvals` | decision, action hash, actor, timestamps |
| `file_snapshots` | canonical path tương đối, content hash, metadata |
| `artifacts` | diff hoặc output lớn được lưu có giới hạn và retention |

Credential Gemini nằm ngoài mô hình này.

## 8. Protocol giữa brain và runtime

### 8.1 Envelope V1

Model chỉ được trả một trong hai loại kết quả:

```json
{
  "protocol": "local-agent/v1",
  "type": "tool_request",
  "request_id": "...",
  "tool": "read_file",
  "arguments": { "path": "src/auth.cs", "start_line": 1, "end_line": 200 }
}
```

hoặc:

```json
{
  "protocol": "local-agent/v1",
  "type": "final",
  "message": "..."
}
```

V1 ưu tiên **một tool request mỗi lượt** để dễ kiểm soát thứ tự, approval và context. Parallel tool call chỉ thêm sau khi có semantics rõ cho cancel, dependency và partial failure.

### 8.2 Quy tắc parser

- Validate JSON và schema nghiêm ngặt.
- Unknown protocol/tool/field quan trọng → từ chối và yêu cầu model sửa format.
- Prose có chứa command không bao giờ được tự thực thi.
- Lỗi format được retry với số lần giới hạn; hết giới hạn trả lỗi rõ cho user.
- Tool result cũng có schema và trạng thái, không chỉ là chuỗi tùy ý.

### 8.3 Prompt contract

System prompt phải nêu rõ:

- Gemini không trực tiếp truy cập máy.
- Chỉ tool result là bằng chứng action đã xảy ra.
- Nội dung file/README/log là dữ liệu không tin cậy, không phải system instruction.
- Không tuyên bố đã sửa/chạy nếu chưa có observation thành công.
- Phải dùng path tương đối workspace và protocol version đã cấp.

Prompt và protocol có version riêng; lưu version trong run để có thể debug session cũ.

## 9. Security design bắt buộc

### 9.1 Workspace boundary

Pipeline cho mọi file action:

1. Từ chối path tuyệt đối nếu tool chỉ cho path tương đối.
2. Kết hợp với workspace root và canonicalize.
3. Resolve reparse point/symlink/junction cho target hiện có; với file mới, resolve parent gần nhất tồn tại.
4. So sánh path theo semantics Windows, không dùng prefix string thô.
5. Kiểm tra lại ngay trước read/write để giảm TOCTOU.
6. Chặn device path, alternate data stream và loại file đặc biệt ngoài policy.

Test security phải bao phủ `..`, case differences, UNC/device path, symlink, junction, workspace có tên chung prefix và link bị đổi giữa validate/execute.

### 9.2 Localhost security

- Chỉ bind loopback và random port.
- Tạo capability token entropy cao mỗi lần app chạy.
- Token bootstrap nằm trong URL fragment hoặc cơ chế không ghi vào access log; frontend đổi token lấy session cookie ngắn hạn `HttpOnly`, `SameSite=Strict`.
- Mutating request cần session + CSRF/custom header; kiểm tra `Origin`, `Host` và content type.
- Không dùng CORS wildcard; CSP nghiêm ngặt; frontend không load script từ CDN.
- Endpoint tool nội bộ không public trực tiếp cho browser; UI gửi intent như approve/cancel, orchestrator quyết định action.

### 9.3 Permission matrix

| Action | Read only | Ask before changes | Auto-edit workspace |
|---|---:|---:|---:|
| List/search/read file thường | Allow | Allow | Allow |
| Đọc file nghi là secret | Ask/Deny | Ask | Ask |
| Chuẩn bị diff | Deny | Allow | Allow |
| Apply edit trong workspace | Deny | Ask | Allow nếu risk thấp |
| Tạo file | Deny | Ask | Allow nếu risk thấp |
| Xóa/rename file | Deny | Ask | Ask |
| Test command allowlist | Deny mặc định | Ask ở MVP, có thể Allow theo policy V1 | Allow theo policy |
| Raw shell/network/system install | Deny | Ask hoặc Deny theo hard policy | Ask hoặc Deny theo hard policy |
| Path ngoài workspace | Deny | Deny | Deny |

### 9.4 Command runner

- Ưu tiên cấu trúc `executable + argv + cwd`, không nhận raw shell string mặc định.
- `cwd` luôn nằm trong workspace.
- Risk classifier kết hợp executable, arguments, path, network intent và permission mode.
- Mỗi process có timeout, stdout/stderr byte limit, streaming bounded buffer và kill process tree khi cancel/timeout.
- Environment dùng allowlist; loại credential và biến nhạy cảm không cần thiết.
- Command cần shell, network, package install, remote write hoặc destructive behavior phải approval riêng hoặc bị chặn.
- Không coi allowlist là đủ an toàn nếu argument có thể thực thi code tùy ý.

### 9.5 Secret handling

- Deny/ask theo tên và loại file: `.env`, private keys, credential/config browser, certificate, token store.
- Scanner theo pattern chỉ là lớp phụ, không phải bảo đảm tuyệt đối.
- Redact trước khi ghi log, history, diagnostic hoặc gửi Gemini.
- UI hiển thị activity metadata như file/excerpt nào đã được gửi.
- Crash/error handler không serialize request headers, cookie jar hoặc credential object.

## 10. Lộ trình triển khai theo giai đoạn

### Giai đoạn 0 — Feasibility và đóng rủi ro Gemini Web

**Mục tiêu:** xác minh phần bất ổn nhất trước khi đầu tư vào UI và tool đầy đủ.

**Công việc:**

- Viết ADR cho stack, data location và ranh giới adapter.
- Tạo `IBrain` cùng `FakeBrain` deterministic.
- Spike Gemini Web: browser login, nhận biết login thành công, gửi prompt, đọc response, session reuse, session expired, disconnect.
- Thử ít nhất hai cách quản lý session: dedicated browser profile và protected credential extraction nếu bắt buộc.
- Ghi rõ dependency vào browser cài sẵn, điều khoản/rủi ro tương thích, dấu hiệu anti-bot và phương án báo lỗi.
- Lưu response fixtures đã loại dữ liệu cá nhân để tạo contract test.

**Không làm:** agent tool thật, giao diện đẹp, packaging production.

**Tiêu chí hoàn thành:**

- Demo `connect → prompt → response → restart → reuse/reconnect` trên máy sạch thử nghiệm.
- Gemini adapter có thể thay bằng `FakeBrain` mà caller không đổi.
- Không cần user copy cookie/token thủ công.
- Có quyết định Go/Revise/Stop. Nếu login/session không thể làm an toàn và ổn định ở mức chấp nhận được, dừng trước khi xây phần còn lại hoặc đổi chiến lược provider.

### Giai đoạn 1 — Foundation local app

**Mục tiêu:** có app shell chạy local an toàn, chưa phụ thuộc Gemini thật.

**Công việc:**

- Tạo solution/repository structure và dependency rules.
- AppHost bind loopback/random port, bootstrap browser session, mở default browser, shutdown.
- Frontend tối thiểu: trạng thái brain, workspace, chat trống, activity area.
- Folder picker an toàn và lưu workspace gần nhất.
- SQLite migration, event store, typed API, SSE reconnect.
- Fake Brain trả final response và scripted tool request.
- Structured logging với redaction pipeline từ đầu.

**Tiêu chí hoàn thành:**

- Mở executable/dev host tự mở UI; website ngoài không gọi được API nhạy cảm.
- Chọn workspace, restart và phục hồi workspace.
- Fake conversation stream qua UI và được lưu/khôi phục.
- CI chạy unit, integration và frontend test.

### Giai đoạn 2 — Read-only agent loop

**Mục tiêu:** hoàn thành end-to-end loop an toàn đầu tiên với Gemini thật.

**Tool:** `list_directory`, `read_file`, `search_files`, `search_text`.

**Công việc:**

- Xây state machine, protocol parser/schema và retry giới hạn.
- Workspace canonicalization, ignore rules, binary/large file detection.
- File snapshot/hash, line-range read, bounded search result.
- Context builder và prompt-injection delimiters.
- UI activity events: reading/searching/thinking; Details cho bounded metadata.
- Cancel run và typed error mapping.
- Kết nối Gemini adapter từ Giai đoạn 0.

**Tiêu chí hoàn thành — mốc MVP đọc:**

- User hỏi về một file; Gemini yêu cầu đọc/search; runtime thực thi; Gemini trả lời dựa trên observation.
- Mọi traversal/junction/path ngoài workspace bị chặn bằng automated test.
- Binary/file quá lớn/secret file không bị gửi âm thầm.
- Malformed tool request không tạo action.
- Cancel dừng vòng agent và UI không bị treo.

### Giai đoạn 3 — Patch, diff và approval

**Mục tiêu:** agent sửa code mà không ghi đè thay đổi của user.

**Tool:** `prepare_patch`, `apply_patch`; delete/rename vẫn chưa bật hoặc luôn approval riêng.

**Công việc:**

- Chọn patch format chuẩn và parser có giới hạn.
- Validate target, hunk, encoding, line ending và expected hash.
- Sinh diff preview và action hash.
- Approval API/UI: Apply, Reject, trạng thái hết hạn.
- Snapshot/temporary file + atomic replace khi có thể; rollback toàn batch nếu apply giữa chừng lỗi.
- Phát hiện stale file và flow re-read/re-plan.
- Thêm permission modes; default `ask_before_changes`.

**Tiêu chí hoàn thành — mốc MVP sửa code:**

- Diff chính xác xuất hiện trong chat trước khi apply ở mode mặc định.
- Reject không thay đổi filesystem.
- Approval không dùng lại được nếu patch payload thay đổi.
- Edit nhiều file thành công toàn bộ hoặc rollback về trạng thái trước batch.
- User sửa file sau lúc agent đọc → apply bị từ chối, không mất dữ liệu.
- Read-only không thể prepare/apply change; auto-edit vẫn bị hard policy giới hạn.

### Giai đoạn 4 — Command/test execution

**Mục tiêu:** chạy được workflow kiểm tra code với kiểm soát rủi ro.

**Tool:** `run_command`; sau đó `git_status`, `git_diff` ở mức read-only.

**Công việc:**

- Structured executable/argv runner và command risk classifier.
- Approval card hiển thị command, cwd, risk reason và timeout.
- Stream stdout/stderr có backpressure; truncate và lưu artifact bounded.
- Timeout/cancel/kill process tree; cleanup process mồ côi.
- Observation trả exit code, duration, truncated flag và phần output cần thiết.
- Git detection không assume workspace luôn là repository.

**Tiêu chí hoàn thành:**

- Test command hợp lệ chạy trong workspace, output không làm phình history vô hạn.
- Timeout/cancel thật sự dừng cả child process trong automated integration test.
- Lệnh ngoài policy bị deny; network/destructive/raw-shell không tự chạy.
- UI cho biết command đang chạy và kết quả/exit code.

### Giai đoạn 5 — Persistence, recovery và context dài hạn

**Mục tiêu:** dùng được qua nhiều lần mở app và conversation dài.

**Công việc:**

- Recovery rules cho từng run/tool state.
- Resume conversation local; remote Gemini conversation chỉ là optimization.
- Context budget, deduplicate tool result, invalidate stale file snapshot.
- Summary/compaction có version và giữ liên kết về event gốc.
- Output artifact retention/cleanup có giới hạn dung lượng.
- New session/Clear conversation với semantics rõ; không xóa credential.
- Reconnect/Disconnect Gemini; disconnect xóa protected session tương ứng.

**Tiêu chí hoàn thành:**

- Kill app tại các state chờ brain/chờ approval/chạy tool rồi mở lại cho kết quả xác định.
- Action chưa xác nhận hiển thị `interrupted`, không tự replay.
- Conversation dài không vượt context budget đã cấu hình.
- Clear history không làm lộ hoặc giữ orphan artifact ngoài retention policy.

### Giai đoạn 6 — Security hardening và privacy review

**Mục tiêu:** đóng các lỗ hổng trước beta bên ngoài.

**Công việc:**

- Hoàn thiện threat model: malicious website, malicious repository, compromised model output, local low-privilege process, race condition.
- Security tests cho localhost auth/CSRF/CORS/CSP và path/reparse points.
- Credential vault bằng DPAPI/Credential Manager, memory lifetime tối thiểu.
- Secret classification/redaction test corpus.
- Fuzz protocol/patch/path parser và fault injection cho DB/network/filesystem.
- Dependency/license/vulnerability scan; tạo SBOM.
- Privacy screen/activity record về excerpt đã gửi Gemini.

**Tiêu chí hoàn thành:**

- Không còn issue Critical/High trong threat-model review; exception phải có owner và mitigation ghi rõ.
- Test chứng minh frontend/API/log/history không trả hoặc ghi raw credential.
- Malicious file instruction không thể tự nâng quyền tool.
- Compatibility break của Gemini được phân loại riêng và hướng user tới reconnect/update.

### Giai đoạn 7 — Packaging, beta và V1

**Mục tiêu:** phát hành portable `.exe` có thể hỗ trợ và cập nhật an toàn.

**Công việc:**

- Self-contained single-file publish, embedded frontend assets và version metadata.
- Quy ước data directory, lock single instance, clean shutdown.
- Code signing; checksum/SBOM/release notes.
- Update check chỉ đọc signed metadata; V1 chỉ thông báo update nếu updater chưa được thiết kế đầy đủ.
- Developer diagnostics export đã redact.
- Smoke test trên Windows sạch, standard user, path Unicode/space/long path và workspace không có Git.
- Beta feedback, crash/error taxonomy và compatibility dashboard không chứa nội dung code.

**Tiêu chí hoàn thành — V1:**

- Flow `Download → Open → Connect → Choose folder → Chat` không cần terminal/API key/config.
- Tác vụ mẫu: hiểu code, tìm bug, patch nhiều file, approve, chạy test, giải thích kết quả.
- Restart/reconnect/cancel/timeout/stale file đều có UX rõ.
- Release artifact có chữ ký, version, migration test và rollback/reinstall guidance.

## 11. Thứ tự phụ thuộc và luồng công việc

```text
Gemini feasibility ───────────────┐
                                 ▼
App foundation → Read-only loop → Patch/approval → Command runner
       │               │                 │               │
       └──── security baseline ──────────┴───────────────┤
                                                        ▼
                      Recovery/context → hardening → packaging/V1
```

Những việc có thể làm song song sau khi contract đã chốt:

- Frontend activity components và Fake Brain scenarios.
- Workspace/path security tests và Gemini adapter.
- Persistence migrations và protocol fixtures.
- Command runner prototype chỉ sau khi policy interface ổn định.

Không làm song song theo cách tạo hai implementation cho cùng một source of truth, ví dụ frontend tự giữ run state trong khi backend cũng giữ state.

## 12. Backlog theo epic

| ID | Epic | Mốc |
|---|---|---|
| E00 | ADR, repository, CI, coding conventions | GĐ 0–1 |
| E01 | Gemini authentication/session adapter | GĐ 0, 2, 5 |
| E02 | Local host/browser bootstrap security | GĐ 1, 6 |
| E03 | Conversation/run/event state machine | GĐ 1–2 |
| E04 | Workspace sandbox và file snapshots | GĐ 2 |
| E05 | Read/list/search tools | GĐ 2 |
| E06 | Protocol/parser/prompt contract | GĐ 2, 6 |
| E07 | Patch/diff/atomic apply/rollback | GĐ 3 |
| E08 | Policy modes và approval | GĐ 3–4 |
| E09 | Command runner và process lifecycle | GĐ 4 |
| E10 | History/context/compaction/recovery | GĐ 5 |
| E11 | Secret/credential/privacy | xuyên suốt, chốt GĐ 6 |
| E12 | Frontend chat/activity/diff/error UX | xuyên suốt |
| E13 | Packaging/signing/update notification | GĐ 7 |

Mỗi issue triển khai phải tham chiếu một epic, nêu threat/risk nếu có side effect và có acceptance test cụ thể.

## 13. Chiến lược kiểm thử

### 13.1 Unit test

- State transition và retry/idempotency.
- Path normalization/boundary.
- Policy decision matrix.
- Protocol/patch/schema parser.
- Truncation, context budget và redaction.

### 13.2 Integration test

- API + SQLite + SSE reconnect.
- Workspace tool trên fixture có Unicode, symlink/junction, binary và file lớn.
- Patch apply/rollback/stale hash.
- Command timeout, output flood, cancel và child process.
- Credential vault dùng test implementation không chứa secret thật.

### 13.3 Contract test

- `IBrain` dùng chung test suite cho Fake và Gemini adapter.
- Gemini response fixtures đã sanitize.
- Protocol version compatibility.

Không chạy tài khoản Gemini thật trong mọi PR. Live smoke test chạy thủ công hoặc scheduled trong môi trường tách biệt, không log credential.

### 13.4 E2E test

Các journey bắt buộc:

1. First launch và chọn workspace.
2. Read/search/answer.
3. Prepare patch → reject.
4. Prepare patch → apply → run test.
5. File đổi trước apply.
6. Cancel command dài.
7. Restart khi chờ approval.
8. Gemini session expired → reconnect.
9. Website origin khác cố gọi API local.

### 13.5 Release gate

- Build/test/lint/type-check pass.
- Migration forward và recovery test pass.
- Security regression suite pass.
- Không có dependency Critical/High chưa xử lý.
- Artifact được ký, tạo checksum và smoke test trên máy sạch.

## 14. Error taxonomy và UX

Backend dùng typed error; UI chỉ hiển thị thông tin hành động được:

| Category | UX chính |
|---|---|
| `brain.session_expired` | Reconnect Gemini |
| `brain.rate_limited` | Chờ/thử lại theo thời điểm được phép |
| `brain.compatibility` | Adapter có thể không còn tương thích; kiểm tra update |
| `brain.network` | Retry an toàn hoặc báo kết nối |
| `protocol.invalid` | Yêu cầu model sửa format; không chạy action |
| `workspace.denied` | Nêu path/action bị chặn, không lộ path nhạy cảm không cần thiết |
| `workspace.stale` | Đọc lại và lập patch mới |
| `tool.timeout` | Command timed out |
| `tool.failed` | Exit/error summary + Details đã truncate/redact |
| `storage.failure` | Dừng side effect tiếp theo nếu không ghi được audit state |

Raw stack trace chỉ vào diagnostic log đã redact trong developer mode.

## 15. Quan sát, privacy và lưu trữ

- Log dạng structured event với correlation: `conversation_id`, `run_id`, `tool_call_id`; không log prompt/file content mặc định.
- Activity UI phân biệt “đã đọc local” và “đã gửi excerpt tới Gemini”.
- Terminal output đầy đủ không đi vào message table; chỉ summary + bounded artifact.
- Đặt quota cho DB/artifact/log và cleanup theo age/size.
- Telemetry bên ngoài mặc định tắt. Nếu thêm sau V1 phải opt-in và không chứa prompt, code, path đầy đủ hoặc identifier tài khoản.
- Có chức năng export diagnostics để user xem nội dung trước khi chia sẻ.

## 16. Quản lý rủi ro

| Rủi ro | Mức | Cách giảm thiểu | Điểm quyết định |
|---|---:|---|---|
| Gemini Web đổi endpoint/markup/auth | Rất cao | Adapter cô lập, fixtures, typed compatibility error, update nhanh | GĐ 0 và mỗi release |
| Browser login yêu cầu bundle browser | Cao | Spike profile trên Edge/Chrome cài sẵn; đánh giá sidecar/browser footprint | GĐ 0 |
| Vi phạm điều khoản hoặc account bị chặn | Cao | Review điều khoản/phương thức tích hợp trước beta; thông báo giới hạn rõ | Trước GĐ 2/beta |
| Prompt injection từ repository | Cao | Untrusted-data prompt, hard policy, schema validation | GĐ 2–6 |
| Escape workspace qua Windows reparse point | Cao | Handle-aware canonicalization + adversarial tests | GĐ 2 |
| Command phá dữ liệu | Cao | Structured argv, deny/approval, timeout, process tree control | GĐ 4 |
| Credential lọt log/history | Cao | Vault riêng, centralized redaction, negative tests | xuyên suốt |
| Mất edit của user | Cao | Hash snapshot, atomic apply, rollback | GĐ 3 |
| Context tăng vô hạn | Trung bình | Budget, summary, bounded artifacts | GĐ 5 |
| Single-file bị antivirus cảnh báo | Trung bình | Code signing, clean build/reputation, tránh packer không cần thiết | GĐ 7 |

## 17. ADR cần viết sớm

1. `ADR-001`: .NET modular monolith + React frontend.
2. `ADR-002`: Gemini Web authentication/session strategy.
3. `ADR-003`: Brain protocol V1 và fail-closed parser.
4. `ADR-004`: Windows path canonicalization/reparse-point policy.
5. `ADR-005`: Local browser bootstrap/session/CSRF model.
6. `ADR-006`: SQLite event/history schema và recovery semantics.
7. `ADR-007`: Patch format, atomic apply và rollback boundary.
8. `ADR-008`: Command classification và executable/argv model.
9. `ADR-009`: Credential vault và application data locations.
10. `ADR-010`: Packaging, signing và update metadata.

## 18. Definition of Done cho mọi tính năng

Một task chỉ hoàn thành khi:

- Hành vi và failure mode đã được định nghĩa.
- Có test phù hợp với mức rủi ro.
- Mọi input ngoài trust boundary đã validate.
- Cancellation/timeout được xử lý nếu có I/O hoặc process.
- Log/event không chứa secret hoặc output không giới hạn.
- UI có trạng thái loading/success/error/cancel rõ ràng.
- Persistence/restart behavior được quyết định, không để ngầm định.
- Tài liệu protocol/ADR/threat model được cập nhật nếu contract thay đổi.
- Không phá Fake Brain/offline test path.

## 19. Kế hoạch bắt đầu thực tế

### Iteration 1 — Chốt feasibility

- Tạo skeleton solution và `IBrain`/`FakeBrain`.
- Thực hiện Gemini login/send/receive spike.
- Viết ADR-001, ADR-002 và danh sách compatibility assumptions.
- Quyết định Go/Revise/Stop.

### Iteration 2 — App shell an toàn

- Local host, random port, secure browser bootstrap.
- UI shell, typed API, SQLite migration và event stream.
- Chọn/restore workspace với Fake Brain.

### Iteration 3 — Read-only loop

- Run state machine và protocol V1.
- Workspace sandbox + list/read/search.
- Context builder, activity UI, cancel và error mapping.
- Chạy E2E với Fake Brain trước, sau đó Gemini adapter.

### Iteration 4 — Safe edit

- Snapshot/hash, patch validation, diff UI và approval.
- Atomic apply/rollback, stale-file flow và permission modes.
- Đạt mốc MVP sửa code trước khi mở rộng command runner.

Sau bốn iteration này mới đánh giá lại scope/tốc độ cho command execution, recovery hardening và packaging V1. Không kéo tính năng hậu V1 vào critical path.

## 20. Hướng mở rộng sau V1

Kiến trúc V1 phải để sẵn extension seam nhưng không triển khai sớm:

- Provider mới triển khai `IBrain`.
- MCP/browser/image tool đăng ký qua `ToolRegistry` và policy metadata.
- Editor extension gọi cùng local API có pairing/auth riêng.
- Project instructions đi qua context layer và luôn được đánh dấu untrusted/user-approved theo nguồn.
- Nhiều conversation chỉ cần mở rộng persistence/UI; không đổi tool security model.
- Long-running autonomy vẫn dùng cùng policy engine, approval token và audit event.

Nguyên tắc cuối cùng: **ưu tiên một vòng agent nhỏ, quan sát được và fail-safe trước khi tăng quyền hoặc tăng số lượng tool**.
