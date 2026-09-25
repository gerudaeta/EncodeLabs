import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { InboxService } from './inbox.service';
import { MessageDirection } from './inbox.models';

describe('InboxService', () => {
  let service: InboxService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(InboxService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('lists conversations without a cursor', () => {
    service.listConversations().subscribe();
    const request = httpMock.expectOne('/api/conversations');
    expect(request.request.method).toBe('GET');
    request.flush({ items: [], nextCursor: null });
  });

  it('lists conversations with a before cursor', () => {
    service.listConversations('cursor-1').subscribe();
    const request = httpMock.expectOne('/api/conversations?before=cursor-1');
    expect(request.request.method).toBe('GET');
    request.flush({ items: [], nextCursor: null });
  });

  it('lists messages for a conversation', () => {
    service.listMessages('conv-1', 'cursor-2').subscribe();
    const request = httpMock.expectOne('/api/conversations/conv-1/messages?before=cursor-2');
    expect(request.request.method).toBe('GET');
    request.flush({ items: [], nextCursor: null });
  });

  it('posts a mark-read request', () => {
    service.markRead('conv-1').subscribe();
    const request = httpMock.expectOne('/api/conversations/conv-1/read');
    expect(request.request.method).toBe('POST');
    request.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('posts a reply message', () => {
    service.sendMessage('conv-1', 'hello').subscribe();
    const request = httpMock.expectOne('/api/conversations/conv-1/messages');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ text: 'hello' });
    request.flush({ id: 'm1', telegramMessageId: 1, direction: MessageDirection.Outbound,
      text: 'hello', sentAt: '2026-09-24T10:00:00Z' });
  });
});
