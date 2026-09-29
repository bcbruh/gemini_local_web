import { FormEvent, KeyboardEvent, useEffect, useMemo, useRef, useState } from 'react';
import { LocalApi, parseAppEvent } from './api';
import type { AppEvent, AppState, Conversation } from './types';

export function App() {
  const api = useMemo(() => new LocalApi(), []);
  const [state, setState] = useState<AppState | null>(null);
  const [conversation, setConversation] = useState<Conversation | null>(null);
  const [events, setEvents] = useState<AppEvent[]>([]);
  const [draft, setDraft] = useState('');
  const [brainBusy, setBrainBusy] = useState(false);
  const [workspaceBusy, setWorkspaceBusy] = useState(false);
  const [messageBusy, setMessageBusy] = useState(false);
  const [notice, setNotice] = useState('Đang tạo phiên local an toàn…');
  const eventSource = useRef<EventSource | null>(null);
  const messagesElement = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    let active = true;
    const start = async () => {
      await api.initialize();
      const [nextState, nextConversation] = await Promise.all([
        api.getState(),
        api.getConversation(),
      ]);
      if (!active) return;
      setState(nextState);
      setConversation(nextConversation);
      setNotice('Phiên local đã xác thực');

      const source = api.openEvents(nextState.latestEventSequence);
      eventSource.current = source;
      source.onopen = () => active && setNotice('Phiên local đã xác thực');
      source.onerror = () => active && setNotice('Đang kết nối lại event stream…');
      source.onmessage = async message => {
        const appEvent = parseAppEvent(message);
        if (!active) return;
        setEvents(current => [appEvent, ...current].slice(0, 50));
        if (appEvent.type.startsWith('conversation.')) {
          setConversation(await api.getConversation());
        }
        if (appEvent.type.startsWith('run.') || appEvent.type.startsWith('tool.') ||
            appEvent.type.startsWith('approval.') || appEvent.type.startsWith('settings.')) {
          setState(await api.getState());
        }
      };
    };

    start().catch(error => active && setNotice(toMessage(error)));
    return () => {
      active = false;
      eventSource.current?.close();
    };
  }, [api]);

  useEffect(() => {
    const element = messagesElement.current;
    if (element) {
      element.scrollTop = element.scrollHeight;
    }
  }, [conversation?.messages.length]);

  const connectBrain = async () => {
    setBrainBusy(true);
    try {
      const result = await api.connectBrain();
      setState(current => current ? { ...current, brain: result.brain } : current);
      setNotice(`${result.brain.provider} đã kết nối`);
    } catch (error) {
      setNotice(toMessage(error));
    } finally {
      setBrainBusy(false);
    }
  };

  const pickWorkspace = async () => {
    setWorkspaceBusy(true);
    setNotice('Hộp chọn thư mục đã mở. Hãy chọn hoặc đóng hộp thoại.');
    try {
      const result = await api.pickWorkspace();
      if (result) {
        setState(current => current ? { ...current, workspace: result.workspace } : current);
        setNotice(`Đã chọn ${result.workspace.displayName}`);
      } else {
        setNotice('Không thay đổi workspace');
      }
    } catch (error) {
      setNotice(toMessage(error));
    } finally {
      setWorkspaceBusy(false);
    }
  };

  const sendMessage = async (event: FormEvent) => {
    event.preventDefault();
    const content = draft.trim();
    if (!content) return;
    setMessageBusy(true);
    setDraft('');
    try {
      setConversation(await api.sendMessage(content));
      setNotice('Brain đã trả lời');
    } catch (error) {
      setDraft(content);
      setNotice(toMessage(error));
    } finally {
      setMessageBusy(false);
    }
  };

  const handleComposerKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key === 'Enter' && !event.shiftKey && !event.nativeEvent.isComposing) {
      event.preventDefault();
      event.currentTarget.form?.requestSubmit();
    }
  };

  const cancelRun = async () => {
    try {
      await api.cancelRun();
      setNotice('Đã yêu cầu dừng run');
    } catch (error) {
      setNotice(toMessage(error));
    }
  };

  const decideApproval = async (decision: 'Approved' | 'Rejected') => {
    const approval = state?.pendingApproval;
    if (!approval) return;
    try {
      await api.decideApproval(approval.id, approval.actionHash, decision);
      setState(await api.getState());
      setNotice(decision === 'Approved' ? 'Đã duyệt đúng bản diff này' : 'Đã từ chối thay đổi');
    } catch (error) {
      setNotice(toMessage(error));
    }
  };

  const changePermissionMode = async (permissionMode: string) => {
    try {
      await api.setPermissionMode(permissionMode);
      setState(current => current ? { ...current, permissionMode } : current);
      setNotice('Đã cập nhật chế độ quyền');
    } catch (error) {
      setNotice(toMessage(error));
    }
  };

  const activeRun = state?.latestRun && !isTerminalRun(state.latestRun.status)
    ? state.latestRun
    : null;

  return (
    <main className="app-shell">
      <aside className="sidebar">
        <header className="brand">
          <span className="brand-mark" aria-hidden="true">G</span>
          <div>
            <strong>Local Agent</strong>
            <small>{state ? `${state.brain.provider} · ${state.brain.state}` : 'Đang tải…'}</small>
          </div>
        </header>

        <section className="sidebar-settings" aria-label="Thiết lập ứng dụng">
          <div className="setting-row">
            <span className="setting-label">Brain</span>
            <button className="text-button" type="button" disabled={brainBusy || !state} onClick={connectBrain}>
              {brainBusy ? 'Đang kết nối…' : 'Kết nối'}
            </button>
          </div>

          <div className="workspace-setting">
            <span className="setting-label">Workspace</span>
            <strong>{state?.workspace?.displayName ?? 'Chưa chọn thư mục'}</strong>
            {state?.workspace && <small title={state.workspace.canonicalRoot}>{state.workspace.canonicalRoot}</small>}
            <button type="button" disabled={workspaceBusy || !state} onClick={pickWorkspace}>
              {workspaceBusy ? 'Đang chọn…' : 'Chọn workspace'}
            </button>
          </div>

          <div className="permission-setting">
            <label className="setting-label" htmlFor="permission-mode">Quyền chỉnh sửa</label>
            <select
              id="permission-mode"
              value={state?.permissionMode ?? 'ask_before_changes'}
              disabled={!state || Boolean(activeRun)}
              onChange={event => void changePermissionMode(event.target.value)}
              aria-label={`Quyền chỉnh sửa: ${permissionModeLabel(state?.permissionMode)}`}
            >
              <option value="read_only">Chỉ đọc</option>
              <option value="ask_before_changes">Hỏi trước khi sửa</option>
              <option value="auto_edit_workspace">Tự sửa trong workspace</option>
            </select>
          </div>
        </section>

        <section className="activity-section" aria-label="Hoạt động gần đây">
          <div className="activity-title">
            <span>Hoạt động gần đây</span>
            <small>{events.length}</small>
          </div>
          <div className="activity-scroll">
            <div className="run-status" aria-live="polite">
              <div>
                <span className="setting-label">Run gần nhất</span>
                <strong>{state?.latestRun?.status ?? 'Chưa có run'}</strong>
                {state?.latestRun && <small>{state.latestRun.id.slice(0, 10)}</small>}
              </div>
              {activeRun && (
                <button type="button" className="danger-button compact-button" onClick={cancelRun}>
                  Dừng
                </button>
              )}
            </div>
            {state?.latestRun?.errorCategory && (
              <p className="run-error" role="alert">
                {runErrorMessage(state.latestRun.errorCategory)}
              </p>
            )}
            {state?.latestToolCalls.length ? (
              <ol className="tool-list" aria-label="Tool calls của run gần nhất">
                {state.latestToolCalls.map((call, index) => (
                  <li key={`${index}-${call.createdAt}`}>
                    <strong>{call.tool}</strong>
                    <span>{call.status}</span>
                  </li>
                ))}
              </ol>
            ) : null}
            <ol className="event-list">
              {events.length ? events.map(appEvent => (
                <li key={appEvent.sequence}>
                  <span>#{appEvent.sequence}</span>
                  <strong>{appEvent.type}</strong>
                </li>
              )) : <li className="event-empty">Chưa có hoạt động mới.</li>}
            </ol>
          </div>
        </section>

        <p className="app-notice" role="status">{notice}</p>
      </aside>

      <section className="chat-view">
        <header className="chat-header">
          <div>
            <h1>{conversation?.title ?? 'Main conversation'}</h1>
            <small>{conversation?.messages.length ?? 0} tin nhắn</small>
          </div>
          <span
            className={`connection-dot ${state?.brain.isConnected ? 'is-connected' : ''}`}
            title={state?.brain.isConnected ? 'Brain đã kết nối' : 'Brain chưa kết nối'}
          />
        </header>

        {state?.pendingApproval && (
          <section className="approval-panel" aria-label="Thay đổi đang chờ duyệt">
            <div className="approval-heading">
              <div>
                <span className="setting-label">Approval · {state.pendingApproval.tool}</span>
                <h2>{state.pendingApproval.title}</h2>
                <small>Action {state.pendingApproval.actionHash.slice(0, 12)}</small>
              </div>
              <div className="approval-actions">
                <button type="button" className="danger-button" onClick={() => void decideApproval('Rejected')}>
                  Từ chối
                </button>
                <button type="button" onClick={() => void decideApproval('Approved')}>
                  Áp dụng diff này
                </button>
              </div>
            </div>
            <pre className="diff-preview"><code>{state.pendingApproval.diff}</code></pre>
          </section>
        )}

        <div className="messages" aria-live="polite" ref={messagesElement}>
          <div className="messages-inner">
            {conversation?.messages.length ? conversation.messages.map(message => (
              <article className={`message message-${message.role}`} key={message.id}>
                <span className="message-role">{message.role === 'assistant' ? 'Brain' : 'Bạn'}</span>
                <p>{message.content}</p>
              </article>
            )) : (
              <div className="empty-state">
                <span className="empty-mark" aria-hidden="true">G</span>
                <h2>Conversation đang trống</h2>
                <p>Gửi một tin nhắn để bắt đầu làm việc với workspace.</p>
              </div>
            )}
          </div>
        </div>

        <div className="composer-area">
          <form className="composer" onSubmit={sendMessage}>
            <label htmlFor="message">Tin nhắn</label>
            <textarea
              id="message"
              value={draft}
              onChange={event => setDraft(event.target.value)}
              onKeyDown={handleComposerKeyDown}
              placeholder="Hỏi Brain về workspace…"
              rows={1}
              maxLength={32_000}
            />
            <button
              type="submit"
              disabled={messageBusy || !draft.trim() || !state?.brain.isConnected}
              aria-label="Gửi tin nhắn"
            >
              {messageBusy ? '…' : '↑'}
            </button>
          </form>
          <small>Enter để gửi · Shift + Enter để xuống dòng</small>
        </div>
      </section>
    </main>
  );
}

function isTerminalRun(status: string): boolean {
  return ['completed', 'failed', 'cancelled', 'interrupted'].includes(status);
}

function runErrorMessage(category: string): string {
  const messages: Record<string, string> = {
    'brain.authentication': 'Không thể xác thực Brain. Hãy kết nối lại rồi thử tiếp.',
    'brain.session_expired': 'Phiên Brain đã hết hạn. Hãy kết nối lại rồi thử tiếp.',
    'brain.rate_limited': 'Brain đang giới hạn lượt gửi. Hãy chờ một lúc trước khi thử lại.',
    'brain.network': 'Không kết nối được tới Brain. Hãy kiểm tra mạng rồi thử lại.',
    'brain.compatibility': 'Giao diện Gemini đã thay đổi và adapter hiện không tương thích.',
    'brain.invalid_response': 'Brain trả về phản hồi không hợp lệ.',
    'brain.unavailable': 'Brain đang tạm thời không khả dụng.',
    'context.limit': 'Nội dung lượt này vượt giới hạn context. Hãy thu hẹp yêu cầu.',
    'protocol.invalid': 'Brain không trả đúng định dạng agent sau các lần thử lại.',
    'run.tool_turn_limit': 'Run đã dùng hết số lượt đọc/tìm kiếm cho phép.',
    'tool.failed': 'Một công cụ local đã thất bại.',
    'tool.timeout': 'Run đã quá thời gian cho phép.',
  };
  return messages[category] ?? `Run thất bại: ${category}`;
}

function permissionModeLabel(mode?: string): string {
  return {
    read_only: 'Chỉ đọc',
    ask_before_changes: 'Hỏi trước khi sửa',
    auto_edit_workspace: 'Tự sửa trong workspace',
  }[mode ?? ''] ?? 'Đang tải…';
}

function toMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'Đã xảy ra lỗi không xác định.';
}
