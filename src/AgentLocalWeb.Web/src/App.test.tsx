import { fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { App } from './App';

let latestRun: Record<string, unknown> | null;
let pendingApproval: Record<string, unknown> | null;
let brainConnected: boolean;
let workspaceRequestPending: boolean;

class FakeEventSource {
  onopen: (() => void) | null = null;
  onerror: (() => void) | null = null;
  onmessage: ((event: MessageEvent<string>) => void) | null = null;

  constructor(public readonly url: string) {}

  close() {}
}

describe('App', () => {
  beforeEach(() => {
    latestRun = null;
    pendingApproval = null;
    brainConnected = false;
    workspaceRequestPending = false;
    window.location.hash = '#bootstrap=test-token';
    vi.stubGlobal('EventSource', FakeEventSource);
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.endsWith('/api/workspace/pick') && workspaceRequestPending) {
        return new Promise<Response>(() => {});
      }
      const payload = url.endsWith('/api/session/bootstrap')
        ? { csrfToken: 'csrf-test' }
        : url.endsWith('/api/state')
          ? {
              brain: {
                provider: 'fake',
                state: brainConnected ? 'Connected' : 'Disconnected',
                isConnected: brainConnected,
              },
              workspace: null,
              latestRun,
              latestToolCalls: [{
                tool: 'read_file',
                status: 'succeeded',
                createdAt: '2026-09-29T00:00:00Z',
                completedAt: '2026-09-29T00:00:01Z',
                errorCode: null,
              }],
              pendingApproval,
              permissionMode: 'ask_before_changes',
              latestEventSequence: 3,
            }
          : url.endsWith('/api/conversation')
            ? {
                id: 'conversation-1',
                title: 'Main conversation',
                createdAt: '2026-09-29T00:00:00Z',
                updatedAt: '2026-09-29T00:00:00Z',
                messages: [],
              }
            : null;
      return {
        ok: true,
        status: 200,
        json: async () => payload,
      } as Response;
    }));
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    window.location.hash = '';
  });

  it('bootstraps the local session and renders restored state', async () => {
    render(<App />);

    expect(await screen.findByText('fake · Disconnected')).toBeInTheDocument();
    expect(screen.getByText('Chưa chọn thư mục')).toBeInTheDocument();
    expect(screen.getByText('Conversation đang trống')).toBeInTheDocument();
    expect(screen.getByText('read_file')).toBeInTheDocument();
    expect(fetch).toHaveBeenCalledWith(
      '/api/session/bootstrap',
      expect.objectContaining({ method: 'POST' }),
    );
  });

  it('renders an actionable message for a persisted run error', async () => {
    latestRun = {
      id: 'run-1',
      conversationId: 'conversation-1',
      status: 'failed',
      protocolVersion: 'local-agent/v1',
      promptVersion: 'agent-system/v1',
      createdAt: '2026-09-29T00:00:00Z',
      updatedAt: '2026-09-29T00:00:01Z',
      completedAt: '2026-09-29T00:00:01Z',
      errorCategory: 'brain.rate_limited',
      cancelRequested: false,
    };

    render(<App />);

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Brain đang giới hạn lượt gửi',
    );
  });

  it('keeps chat and Brain controls available while the workspace picker is open', async () => {
    brainConnected = true;
    workspaceRequestPending = true;
    render(<App />);

    fireEvent.click(await screen.findByRole('button', { name: 'Chọn workspace' }));

    expect(await screen.findByRole('button', { name: 'Đang chọn…' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Kết nối' })).toBeEnabled();
    fireEvent.change(screen.getByLabelText('Tin nhắn'), { target: { value: 'hello' } });
    expect(screen.getByRole('button', { name: 'Gửi tin nhắn' })).toBeEnabled();
  });

  it('renders the exact pending diff with approve and reject actions', async () => {
    pendingApproval = {
      id: 'approval-1',
      runId: 'run-1',
      requestId: 'apply-1',
      tool: 'apply_patch',
      actionHash: 'a'.repeat(64),
      title: 'Apply changes to 1 file(s)',
      diff: '--- a/file.txt\n+++ b/file.txt\n-old\n+new',
      status: 'requested',
      createdAt: '2026-09-29T00:00:00Z',
      decidedAt: null,
    };

    render(<App />);

    expect(await screen.findByLabelText('Thay đổi đang chờ duyệt')).toHaveTextContent('-old');
    expect(screen.getByRole('button', { name: 'Áp dụng diff này' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Từ chối' })).toBeInTheDocument();
  });
});
