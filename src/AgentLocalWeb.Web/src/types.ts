export interface BrainStatus {
  provider: string;
  state: string;
  isConnected: boolean;
}

export interface WorkspaceSelection {
  canonicalRoot: string;
  displayName: string;
}

export interface AppState {
  brain: BrainStatus;
  workspace: WorkspaceSelection | null;
  latestRun: AgentRun | null;
  latestToolCalls: ToolCall[];
  pendingApproval: Approval | null;
  permissionMode: string;
  latestEventSequence: number;
}

export interface Approval {
  id: string;
  runId: string;
  requestId: string;
  tool: string;
  actionHash: string;
  title: string;
  diff: string;
  status: 'requested';
  createdAt: string;
  decidedAt: null;
}

export interface ToolCall {
  tool: string;
  status: 'requested' | 'succeeded' | 'failed' | 'cancelled' | 'interrupted';
  createdAt: string;
  completedAt: string | null;
  errorCode: string | null;
}

export interface AgentRun {
  id: string;
  conversationId: string;
  status: string;
  protocolVersion: string;
  promptVersion: string;
  createdAt: string;
  updatedAt: string;
  completedAt: string | null;
  errorCategory: string | null;
  cancelRequested: boolean;
}

export interface ConversationMessage {
  id: string;
  conversationId: string;
  role: 'system' | 'user' | 'assistant' | 'tool';
  content: string;
  createdAt: string;
  ordinal: number;
}

export interface Conversation {
  id: string;
  title: string;
  createdAt: string;
  updatedAt: string;
  messages: ConversationMessage[];
}

export interface AppEvent {
  sequence: number;
  occurredAt: string;
  type: string;
  payload: Record<string, unknown>;
}
