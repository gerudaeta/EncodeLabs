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
    <main class="inbox">
      <section class="conversations">
        <h1>Conversations</h1>
        @if (conversationsLoading() && conversations().length === 0) {
          <p>Loading conversations…</p>
        } @else if (conversationsError()) {
          <p class="error">{{ conversationsError() }}</p>
        } @else if (conversations().length === 0) {
          <p>No conversations yet.</p>
        }
        <ul>
          @for (conversation of conversations(); track conversation.id) {
            <li>
              <button type="button" [attr.data-testid]="'conversation-' + conversation.id"
                [class.selected]="conversation.id === selectedId()"
                (click)="selectConversation(conversation.id)">
                <strong>{{ conversation.displayName ?? conversation.telegramChatId }}</strong>
                <span class="preview">{{ conversation.lastMessagePreview }}</span>
              </button>
            </li>
          }
        </ul>
        @if (conversationsCursor()) {
          <button type="button" data-testid="load-more-conversations" (click)="loadMoreConversations()"
            [disabled]="conversationsLoading()">
            Load more
          </button>
        }
      </section>

      <section class="thread">
        @if (!selectedId()) {
          <p>Select a conversation to see its messages.</p>
        } @else {
          @if (messagesCursor()) {
            <button type="button" data-testid="load-more-messages" (click)="loadMoreMessages()"
              [disabled]="messagesLoading()">
              Load older messages
            </button>
          }
          @if (messagesLoading() && messages().length === 0) {
            <p>Loading messages…</p>
          } @else if (messagesError()) {
            <p class="error">{{ messagesError() }}</p>
          } @else if (messages().length === 0) {
            <p>No messages yet.</p>
          }
          <ul class="messages">
            @for (message of messages(); track message.id) {
              <li [attr.data-testid]="'message-' + message.id" [class]="message.direction">
                <span class="text">{{ message.text }}</span>
                <span class="time">{{ message.sentAt | date: 'short' }}</span>
              </li>
            }
          </ul>

          <form class="composer" (submit)="$event.preventDefault(); sendReply()">
            <textarea [ngModel]="replyText()" (ngModelChange)="replyText.set($event)" name="replyText"
              [disabled]="sending()" maxlength="4096" placeholder="Type a reply…"></textarea>
            @if (sendError()) {
              <p class="error">{{ sendError() }}</p>
            }
            <button type="submit" data-testid="send-reply" [disabled]="sending() || !replyText().trim()">
              {{ sending() ? 'Sending…' : 'Send' }}
            </button>
          </form>
        }
      </section>
    </main>
  `,
  styles: `
    .inbox { display: flex; gap: 1rem; height: 100vh; box-sizing: border-box; padding: 1rem; }
    .conversations { flex: 1; max-width: 320px; overflow-y: auto; }
    .thread { flex: 2; display: flex; flex-direction: column; overflow-y: auto; }
    .conversations ul, .messages { list-style: none; margin: 0; padding: 0; }
    .conversations button { display: block; width: 100%; text-align: left; padding: 0.5rem;
      background: none; border: 1px solid #ddd; margin-bottom: 0.25rem; cursor: pointer; }
    .conversations button.selected { background: #eef; }
    .preview { display: block; color: #666; font-size: 0.85rem; }
    .messages li { margin: 0.25rem 0; padding: 0.5rem; border-radius: 0.5rem; max-width: 70%; }
    .messages li.inbound { background: #f0f0f0; align-self: flex-start; }
    .messages li.outbound { background: #dbeafe; align-self: flex-end; margin-left: auto; }
    .composer { display: flex; flex-direction: column; gap: 0.5rem; margin-top: 1rem; }
    .error { color: #b00020; }
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

  private loadConversations(): void {
    this.conversationsLoading.set(true);
    this.conversationsError.set(null);
    this.inboxService.listConversations().subscribe({
      next: (page) => {
        this.conversations.set(page.items);
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
    if (this.selectedId() === conversationId) this.loadMessages(conversationId);
  }
}
