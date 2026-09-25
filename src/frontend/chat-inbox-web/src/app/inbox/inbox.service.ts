import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { Conversation, Message, Page } from './inbox.models';

@Injectable({ providedIn: 'root' })
export class InboxService {
  private readonly http = inject(HttpClient);

  listConversations(before?: string): Observable<Page<Conversation>> {
    return this.http.get<Page<Conversation>>('/api/conversations', { params: cursorParams(before) });
  }

  listMessages(conversationId: string, before?: string): Observable<Page<Message>> {
    return this.http.get<Page<Message>>(`/api/conversations/${conversationId}/messages`,
      { params: cursorParams(before) });
  }

  sendMessage(conversationId: string, text: string): Observable<Message> {
    return this.http.post<Message>(`/api/conversations/${conversationId}/messages`, { text });
  }
}

function cursorParams(before?: string): HttpParams {
  return before ? new HttpParams().set('before', before) : new HttpParams();
}
