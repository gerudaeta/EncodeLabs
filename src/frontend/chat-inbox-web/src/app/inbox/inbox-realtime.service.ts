import { Injectable } from '@angular/core';
import { HubConnection, HubConnectionBuilder } from '@microsoft/signalr';
import { Observable, Subject } from 'rxjs';

export interface MessageStoredNotification {
  conversationId: string;
}

@Injectable({ providedIn: 'root' })
export class InboxRealtimeService {
  private connection: HubConnection | null = null;
  private readonly messageStoredSubject = new Subject<MessageStoredNotification>();
  readonly messageStored$: Observable<MessageStoredNotification> = this.messageStoredSubject.asObservable();

  connect(): void {
    if (this.connection) return;
    this.connection = new HubConnectionBuilder()
      .withUrl('/hubs/inbox')
      .withAutomaticReconnect()
      .build();
    this.connection.on('messageStored', (notification: MessageStoredNotification) =>
      this.messageStoredSubject.next(notification));
    void this.connection.start().catch(() => {
      // withAutomaticReconnect() handles retrying; nothing else to do on the initial failure.
    });
  }
}
