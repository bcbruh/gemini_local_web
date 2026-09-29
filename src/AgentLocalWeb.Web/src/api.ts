import type { AppEvent, AppState, Conversation, WorkspaceSelection } from './types';

interface SessionResponse {
  csrfToken: string;
}

interface WorkspaceResponse {
  workspace: WorkspaceSelection;
}

interface BrainResponse {
  brain: AppState['brain'];
}

export class LocalApi {
  private csrfToken: string | null = null;

  async initialize(): Promise<void> {
    const fragment = new URLSearchParams(window.location.hash.slice(1));
    const bootstrap = fragment.get('bootstrap');
    const session = bootstrap
      ? await this.request<SessionResponse>('/api/session/bootstrap', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ token: bootstrap }),
        }, false)
      : await this.request<SessionResponse>('/api/session', {}, false);
    this.csrfToken = session.csrfToken;
    if (bootstrap) {
      window.history.replaceState(null, '', '/');
    }
  }

  getState(): Promise<AppState> {
    return this.request('/api/state');
  }

  getConversation(): Promise<Conversation> {
    return this.request('/api/conversation');
  }

  connectBrain(): Promise<BrainResponse> {
    return this.request('/api/brain/connect', { method: 'POST' });
  }

  pickWorkspace(): Promise<WorkspaceResponse | null> {
    return this.request('/api/workspace/pick', { method: 'POST' });
  }

  sendMessage(content: string): Promise<Conversation> {
    return this.request('/api/conversation/messages', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ content }),
    });
  }

  cancelRun(): Promise<{ runId: string }> {
    return this.request('/api/runs/cancel', { method: 'POST' });
  }

  decideApproval(
    approvalId: string,
    actionHash: string,
    decision: 'Approved' | 'Rejected',
  ): Promise<{ id: string; status: string }> {
    return this.request(`/api/approvals/${encodeURIComponent(approvalId)}/decision`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ actionHash, decision }),
    });
  }

  setPermissionMode(permissionMode: string): Promise<{ permissionMode: string }> {
    return this.request('/api/settings/permission-mode', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ permissionMode }),
    });
  }

  openEvents(after: number): EventSource {
    return new EventSource(`/api/events?after=${after}`);
  }

  private async request<T>(
    path: string,
    options: RequestInit = {},
    includeCsrf = true,
  ): Promise<T> {
    const headers = new Headers(options.headers);
    const method = options.method?.toUpperCase() ?? 'GET';
    if (includeCsrf && this.csrfToken && !['GET', 'HEAD', 'OPTIONS'].includes(method)) {
      headers.set('X-AgentLocalWeb-CSRF', this.csrfToken);
    }

    const response = await fetch(path, { ...options, headers, credentials: 'same-origin' });
    if (!response.ok) {
      const details = await response.json().catch(() => null) as { error?: string } | null;
      throw new Error(details?.error ?? `Local API failed (${response.status})`);
    }

    return response.status === 204 ? null as T : response.json() as Promise<T>;
  }
}

export function parseAppEvent(message: MessageEvent<string>): AppEvent {
  return JSON.parse(message.data) as AppEvent;
}
