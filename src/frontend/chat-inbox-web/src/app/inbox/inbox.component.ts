import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { InboxRealtimeService } from './inbox-realtime.service';
import { Conversation, Message } from './inbox.models';
import { InboxService } from './inbox.service';

@Component({
  selector: 'app-inbox',
  imports: [DatePipe, FormsModule],
  template: `
    <main class="flex h-screen bg-brand-5 text-sm text-neutral-900 antialiased dark:bg-neutral-950 dark:text-neutral-100">
      <aside class="flex w-80 shrink-0 flex-col border-r border-neutral-200 bg-white dark:border-neutral-800 dark:bg-neutral-900">
        <header class="flex items-center justify-between border-b border-neutral-200 px-5 py-4 dark:border-neutral-800">
          <h1 class="text-lg font-semibold tracking-tight">Conversations</h1>
          <span class="min-w-6 rounded-full bg-brand-5 px-2 py-0.5 text-center text-xs font-semibold text-brand-50 dark:bg-brand-50/25 dark:text-brand-10">
            {{ conversations().length }}
          </span>
        </header>
        <div class="flex-1 overflow-y-auto p-2">
          @if (conversationsLoading() && conversations().length === 0) {
            <p class="m-4 text-center text-neutral-500">Loading conversations…</p>
          } @else if (conversationsError()) {
            <p class="m-4 text-center text-rose-600">{{ conversationsError() }}</p>
          } @else if (conversations().length === 0) {
            <p class="m-4 text-center text-neutral-500">No conversations yet.</p>
          }
          <ul>
            @for (conversation of conversations(); track conversation.id) {
              <li>
                <button type="button" [attr.data-testid]="'conversation-' + conversation.id"
                  class="flex w-full cursor-pointer items-center gap-3 rounded-xl px-3 py-2.5 text-left transition-colors hover:bg-neutral-100 focus-visible:outline-2 focus-visible:outline-brand-50 dark:hover:bg-neutral-800"
                  [class.!bg-brand-10/60]="conversation.id === selectedId()"
                  [class.dark:!bg-brand-50/15]="conversation.id === selectedId()"
                  (click)="selectConversation(conversation.id)">
                  <span class="grid size-10 shrink-0 place-items-center rounded-full bg-linear-135 from-brand-50 to-brand-indigo font-semibold text-white">
                    {{ initials(conversation) }}
                  </span>
                  <span class="flex min-w-0 flex-1 flex-col">
                    <span class="flex items-baseline justify-between gap-2">
                      <strong class="truncate font-semibold">{{ conversation.displayName ?? conversation.telegramChatId }}</strong>
                      <span class="flex shrink-0 items-center gap-1.5">
                        @if (conversation.unreadCount > 0) {
                          <span [attr.data-testid]="'unread-badge-' + conversation.id"
                            class="min-w-5 rounded-full bg-brand-50 px-1.5 py-0.5 text-center text-[0.65rem] font-semibold text-white">
                            {{ conversation.unreadCount }}
                          </span>
                        }
                        @if (conversation.lastMessageAt) {
                          <time class="text-xs text-neutral-400">{{ conversation.lastMessageAt | date: 'shortTime' }}</time>
                        }
                      </span>
                    </span>
                    <span class="truncate text-neutral-500 dark:text-neutral-400">{{ conversation.lastMessagePreview }}</span>
                  </span>
                </button>
              </li>
            }
          </ul>
          @if (conversationsCursor()) {
            <button type="button" data-testid="load-more-conversations" (click)="loadMoreConversations()"
              class="mt-2 w-full cursor-pointer rounded-lg border border-dashed border-neutral-300 py-2 text-neutral-500 hover:border-brand-50 hover:text-brand-50 disabled:opacity-50 dark:border-neutral-700"
              [disabled]="conversationsLoading()">
              Load more
            </button>
          }
        </div>
      </aside>

      <section class="flex min-w-0 flex-1 flex-col">
        @if (!selectedId()) {
          <div class="grid flex-1 place-content-center justify-items-center gap-1 text-neutral-500">
            <span class="text-4xl" aria-hidden="true">💬</span>
            <p>Select a conversation to see its messages.</p>
          </div>
        } @else {
          <header class="flex items-center gap-3 border-b border-neutral-200 bg-white px-6 py-3 dark:border-neutral-800 dark:bg-neutral-900">
            @if (selectedConversation(); as conversation) {
              <span class="grid size-9 shrink-0 place-items-center rounded-full bg-linear-135 from-brand-50 to-brand-indigo text-sm font-semibold text-white">
                {{ initials(conversation) }}
              </span>
              <span class="flex flex-col">
                <strong class="font-semibold">{{ conversation.displayName ?? conversation.telegramChatId }}</strong>
                <span class="text-xs text-neutral-500 dark:text-neutral-400">Telegram · {{ conversation.telegramChatId }}</span>
              </span>
            }
          </header>

          <!-- flex-col-reverse keeps the view pinned to the newest message without JS -->
          <div class="flex flex-1 flex-col-reverse overflow-y-auto">
            <div class="flex flex-col p-6">
              @if (messagesCursor()) {
                <button type="button" data-testid="load-more-messages" (click)="loadMoreMessages()"
                  class="mb-4 cursor-pointer self-center rounded-full border border-dashed border-neutral-300 px-4 py-1.5 text-neutral-500 hover:border-brand-50 hover:text-brand-50 disabled:opacity-50 dark:border-neutral-700"
                  [disabled]="messagesLoading()">
                  Load older messages
                </button>
              }
              @if (messagesLoading() && messages().length === 0) {
                <p class="m-4 text-center text-neutral-500">Loading messages…</p>
              } @else if (messagesError()) {
                <p class="m-4 text-center text-rose-600">{{ messagesError() }}</p>
              } @else if (messages().length === 0) {
                <p class="m-4 text-center text-neutral-500">No messages yet.</p>
              }
              <ul class="flex flex-col gap-1.5">
                @for (message of messages(); track message.id) {
                  <li [attr.data-testid]="'message-' + message.id" [class]="message.direction"
                    class="flex max-w-[min(70%,560px)] flex-col rounded-2xl px-3.5 py-2 whitespace-pre-wrap wrap-anywhere shadow-xs
                      [&.inbound]:self-start [&.inbound]:rounded-bl-md [&.inbound]:border [&.inbound]:border-neutral-200 [&.inbound]:bg-white
                      dark:[&.inbound]:border-neutral-800 dark:[&.inbound]:bg-neutral-900
                      [&.outbound]:self-end [&.outbound]:rounded-br-md [&.outbound]:bg-brand-50 [&.outbound]:text-white">
                    <span>{{ message.text }}</span>
                    <time class="mt-0.5 self-end text-[0.68rem] opacity-65">{{ message.sentAt | date: 'short' }}</time>
                  </li>
                }
              </ul>
            </div>
          </div>

          <form class="px-6 pt-3 pb-5" (submit)="$event.preventDefault(); sendReply()">
            @if (sendError()) {
              <p class="mb-2 text-rose-600">{{ sendError() }}</p>
            }
            <div class="flex items-end gap-2 rounded-3xl border border-neutral-200 bg-white py-1.5 pr-1.5 pl-4 transition focus-within:border-brand-50 focus-within:ring-3 focus-within:ring-brand-50/15 dark:border-neutral-800 dark:bg-neutral-900">
              <textarea [ngModel]="replyText()" (ngModelChange)="replyText.set($event)" name="replyText"
                class="max-h-32 flex-1 resize-none bg-transparent py-2 outline-none field-sizing-content placeholder:text-neutral-400"
                [disabled]="sending()" maxlength="4096" rows="1" placeholder="Type a reply…"
                (keydown.enter)="onEnter($event)"></textarea>
              <button type="submit" data-testid="send-reply"
                class="grid size-10 shrink-0 cursor-pointer place-items-center rounded-full bg-brand-50 text-white transition hover:scale-105 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand-50 disabled:cursor-default disabled:opacity-40 disabled:hover:scale-100"
                [disabled]="sending() || !replyText().trim()" [attr.aria-label]="sending() ? 'Sending…' : 'Send'">
                <svg viewBox="0 0 24 24" class="size-5" aria-hidden="true">
                  <path fill="currentColor" d="M3.4 20.4 21 12 3.4 3.6 3.4 10l12.6 2-12.6 2z" />
                </svg>
              </button>
            </div>
          </form>
        }
      </section>
    </main>
  `,
})
export class InboxComponent implements OnInit {
  private readonly inboxService = inject(InboxService);
  private readonly realtime = inject(InboxRealtimeService);

  readonly conversations = signal<Conversation[]>([]);
  readonly conversationsLoading = signal(false);
  readonly conversationsError = signal<string | null>(null);
  readonly conversationsCursor = signal<string | null>(null);

  readonly selectedId = signal<string | null>(null);
  readonly messages = signal<Message[]>([]);
  readonly messagesLoading = signal(false);
  readonly messagesError = signal<string | null>(null);
  readonly messagesCursor = signal<string | null>(null);

  readonly replyText = signal('');
  readonly sending = signal(false);
  readonly sendError = signal<string | null>(null);

  readonly selectedConversation = computed(() =>
    this.conversations().find((c) => c.id === this.selectedId()) ?? null);

  ngOnInit(): void {
    this.loadConversations();
    this.realtime.messageStored$.subscribe((notification) => this.onMessageStored(notification.conversationId));
    this.realtime.connect();
  }

  loadMoreConversations(): void {
    const cursor = this.conversationsCursor();
    if (!cursor || this.conversationsLoading()) return;
    this.conversationsLoading.set(true);
    this.inboxService.listConversations(cursor).subscribe({
      next: (page) => {
        this.conversations.set([...this.conversations(), ...page.items]);
        this.conversationsCursor.set(page.nextCursor);
        this.conversationsLoading.set(false);
      },
      error: () => {
        this.conversationsError.set('Could not load more conversations.');
        this.conversationsLoading.set(false);
      },
    });
  }

  selectConversation(id: string): void {
    if (this.selectedId() === id) return;
    this.selectedId.set(id);
    this.messages.set([]);
    this.messagesCursor.set(null);
    this.sendError.set(null);
    this.replyText.set('');
    this.loadMessages(id);
    this.markRead(id);
  }

  loadMoreMessages(): void {
    const id = this.selectedId();
    const cursor = this.messagesCursor();
    if (!id || !cursor || this.messagesLoading()) return;
    this.messagesLoading.set(true);
    this.inboxService.listMessages(id, cursor).subscribe({
      next: (page) => {
        this.messages.set([...page.items, ...this.messages()]);
        this.messagesCursor.set(page.nextCursor);
        this.messagesLoading.set(false);
      },
      error: () => {
        this.messagesError.set('Could not load more messages.');
        this.messagesLoading.set(false);
      },
    });
  }

  sendReply(): void {
    const id = this.selectedId();
    const text = this.replyText().trim();
    if (!id || !text || this.sending()) return;
    this.sending.set(true);
    this.sendError.set(null);
    this.inboxService.sendMessage(id, text).subscribe({
      next: (message) => {
        this.messages.set([...this.messages(), message]);
        this.replyText.set('');
        this.sending.set(false);
        this.loadConversations();
      },
      error: () => {
        this.sendError.set('Could not send the reply.');
        this.sending.set(false);
      },
    });
  }

  initials(conversation: Conversation): string {
    const name = conversation.displayName?.trim();
    if (!name) return '#';
    return name.split(/\s+/).slice(0, 2).map((part) => part[0]).join('').toUpperCase();
  }

  onEnter(event: Event): void {
    if ((event as KeyboardEvent).shiftKey) return;
    event.preventDefault();
    this.sendReply();
  }

  private loadConversations(): void {
    this.conversationsLoading.set(true);
    this.conversationsError.set(null);
    this.inboxService.listConversations().subscribe({
      next: (page) => {
        // The currently open conversation is always shown as read, even if this response
        // raced a still-in-flight mark-read call.
        const selectedId = this.selectedId();
        this.conversations.set(page.items.map((c) =>
          (c.id === selectedId ? { ...c, unreadCount: 0 } : c)));
        this.conversationsCursor.set(page.nextCursor);
        this.conversationsLoading.set(false);
      },
      error: () => {
        this.conversationsError.set('Could not load conversations.');
        this.conversationsLoading.set(false);
      },
    });
  }

  private loadMessages(id: string): void {
    this.messagesLoading.set(true);
    this.messagesError.set(null);
    this.inboxService.listMessages(id).subscribe({
      next: (page) => {
        this.messages.set(page.items);
        this.messagesCursor.set(page.nextCursor);
        this.messagesLoading.set(false);
      },
      error: () => {
        this.messagesError.set('Could not load messages.');
        this.messagesLoading.set(false);
      },
    });
  }

  private onMessageStored(conversationId: string): void {
    this.loadConversations();
    if (this.selectedId() === conversationId) {
      this.loadMessages(conversationId);
      this.markRead(conversationId);
    }
  }

  private markRead(id: string): void {
    this.conversations.set(this.conversations()
      .map((c) => (c.id === id ? { ...c, unreadCount: 0 } : c)));
    this.inboxService.markRead(id).subscribe({ error: () => {} });
  }
}
