export interface Page<T> {
  items: T[];
  nextCursor: string | null;
}

export interface Conversation {
  id: string;
  telegramChatId: number;
  displayName: string | null;
  lastMessageAt: string | null;
  lastMessagePreview: string | null;
  unreadCount: number;
}

export const enum MessageDirection {
  Inbound = 'inbound',
  Outbound = 'outbound',
}

export interface Message {
  id: string;
  telegramMessageId: number;
  direction: MessageDirection;
  text: string;
  sentAt: string;
}
