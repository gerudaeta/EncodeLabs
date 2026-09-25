import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { InboxComponent } from './inbox.component';
import { InboxRealtimeService, MessageStoredNotification } from './inbox-realtime.service';
import { Conversation, Message, MessageDirection, Page } from './inbox.models';

const conversation = (id: string, preview: string, unreadCount = 0): Conversation => ({
  id, telegramChatId: 1, displayName: `Chat ${id}`, lastMessageAt: '2026-09-24T10:00:00Z',
  lastMessagePreview: preview, unreadCount,
});

const message = (id: string, direction: MessageDirection, text: string): Message => ({
  id, telegramMessageId: 1, direction, text, sentAt: '2026-09-24T10:00:00Z',
});

describe('InboxComponent', () => {
  let fixture: ComponentFixture<InboxComponent>;
  let httpMock: HttpTestingController;
  let messageStored: Subject<MessageStoredNotification>;
  let connect: ReturnType<typeof vi.fn>;

  beforeEach(async () => {
    messageStored = new Subject<MessageStoredNotification>();
    connect = vi.fn();

    await TestBed.configureTestingModule({
      imports: [InboxComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: InboxRealtimeService, useValue: { messageStored$: messageStored.asObservable(), connect } },
      ],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(InboxComponent);
  });

  afterEach(() => httpMock.verify());

  function flushConversations(page: Page<Conversation>): void {
    httpMock.expectOne('/api/conversations').flush(page);
  }

  function flushMarkRead(id: string): void {
    httpMock.expectOne(`/api/conversations/${id}/read`).flush(null, { status: 204, statusText: 'No Content' });
  }

  it('shows a loading state while conversations load', () => {
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Loading conversations');
    httpMock.expectOne('/api/conversations').flush({ items: [], nextCursor: null });
  });

  it('shows an empty state when there are no conversations', () => {
    fixture.detectChanges();
    flushConversations({ items: [], nextCursor: null });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('No conversations yet');
  });

  it('shows an error state when the conversation list fails to load', () => {
    fixture.detectChanges();
    httpMock.expectOne('/api/conversations').flush('boom', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Could not load conversations');
  });

  it('renders conversations and lets the operator select one to load its thread', () => {
    fixture.detectChanges();
    flushConversations({ items: [conversation('c1', 'hi there')], nextCursor: null });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('hi there');

    fixture.nativeElement.querySelector('[data-testid="conversation-c1"]').click();
    fixture.detectChanges();

    httpMock.expectOne('/api/conversations/c1/messages').flush({
      items: [message('m1', MessageDirection.Inbound, 'hello there')], nextCursor: null,
    });
    flushMarkRead('c1');
    fixture.detectChanges();

    const inbound = fixture.nativeElement.querySelector('[data-testid="message-m1"]');
    expect(inbound.textContent).toContain('hello there');
    expect(inbound.className).toContain('inbound');
  });

  it('shows an unread badge and clears it once the conversation is opened', () => {
    fixture.detectChanges();
    flushConversations({ items: [conversation('c1', 'hi there', 3)], nextCursor: null });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="unread-badge-c1"]').textContent).toContain('3');

    fixture.nativeElement.querySelector('[data-testid="conversation-c1"]').click();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="unread-badge-c1"]')).toBeNull();

    httpMock.expectOne('/api/conversations/c1/messages').flush({ items: [], nextCursor: null });
    flushMarkRead('c1');
    fixture.detectChanges();
  });

  it('shows a load more button and appends older conversations', () => {
    fixture.detectChanges();
    flushConversations({ items: [conversation('c1', 'first')], nextCursor: 'cursor-1' });
    fixture.detectChanges();

    fixture.nativeElement.querySelector('[data-testid="load-more-conversations"]').click();
    httpMock.expectOne('/api/conversations?before=cursor-1')
      .flush({ items: [conversation('c2', 'second')], nextCursor: null });
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('second');
  });

  it('sends a reply, disables the composer while sending and appends the result', () => {
    fixture.detectChanges();
    flushConversations({ items: [conversation('c1', 'hi')], nextCursor: null });
    fixture.detectChanges();
    fixture.nativeElement.querySelector('[data-testid="conversation-c1"]').click();
    fixture.detectChanges();
    httpMock.expectOne('/api/conversations/c1/messages').flush({ items: [], nextCursor: null });
    flushMarkRead('c1');
    fixture.detectChanges();

    const component = fixture.componentInstance;
    component.replyText.set('a reply');
    fixture.detectChanges();
    fixture.nativeElement.querySelector('[data-testid="send-reply"]').click();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="send-reply"]').disabled).toBe(true);

    const request = httpMock.expectOne('/api/conversations/c1/messages');
    expect(request.request.method).toBe('POST');
    request.flush(message('m2', MessageDirection.Outbound, 'a reply'));
    fixture.detectChanges();
    httpMock.expectOne('/api/conversations').flush({ items: [conversation('c1', 'a reply')], nextCursor: null });
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('a reply');
    expect(component.replyText()).toBe('');
  });

  it('shows an error and re-enables the composer when sending a reply fails', () => {
    fixture.detectChanges();
    flushConversations({ items: [conversation('c1', 'hi')], nextCursor: null });
    fixture.detectChanges();
    fixture.nativeElement.querySelector('[data-testid="conversation-c1"]').click();
    fixture.detectChanges();
    httpMock.expectOne('/api/conversations/c1/messages').flush({ items: [], nextCursor: null });
    flushMarkRead('c1');
    fixture.detectChanges();

    fixture.componentInstance.replyText.set('a reply');
    fixture.detectChanges();
    fixture.nativeElement.querySelector('[data-testid="send-reply"]').click();
    httpMock.expectOne('/api/conversations/c1/messages')
      .flush('bad gateway', { status: 502, statusText: 'Bad Gateway' });
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Could not send the reply');
    expect(fixture.nativeElement.querySelector('[data-testid="send-reply"]').disabled).toBe(false);
  });

  it('refetches the conversation list and the open thread on a realtime notification', () => {
    fixture.detectChanges();
    flushConversations({ items: [conversation('c1', 'hi')], nextCursor: null });
    fixture.detectChanges();
    fixture.nativeElement.querySelector('[data-testid="conversation-c1"]').click();
    fixture.detectChanges();
    httpMock.expectOne('/api/conversations/c1/messages').flush({ items: [], nextCursor: null });
    flushMarkRead('c1');
    fixture.detectChanges();
    expect(connect).toHaveBeenCalled();

    messageStored.next({ conversationId: 'c1' });

    httpMock.expectOne('/api/conversations').flush({ items: [conversation('c1', 'new one', 1)], nextCursor: null });
    httpMock.expectOne('/api/conversations/c1/messages')
      .flush({ items: [message('m3', MessageDirection.Inbound, 'new one')], nextCursor: null });
    flushMarkRead('c1');
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('new one');
    expect(fixture.nativeElement.querySelector('[data-testid="unread-badge-c1"]')).toBeNull();
  });
});
