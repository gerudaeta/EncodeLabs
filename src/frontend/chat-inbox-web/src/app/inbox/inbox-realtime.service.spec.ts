import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { vi } from 'vitest';
import { InboxRealtimeService } from './inbox-realtime.service';

const handlers = new Map<string, (payload: unknown) => void>();
const fakeConnection = {
  on: vi.fn((event: string, handler: (payload: unknown) => void) => handlers.set(event, handler)),
  start: vi.fn().mockResolvedValue(undefined),
};
const withUrl = vi.fn().mockReturnThis();
const withAutomaticReconnect = vi.fn().mockReturnThis();
const build = vi.fn(() => fakeConnection);

vi.mock('@microsoft/signalr', () => ({
  HubConnectionBuilder: vi.fn(function HubConnectionBuilder(this: unknown) {
    return { withUrl, withAutomaticReconnect, build };
  }),
}));

describe('InboxRealtimeService', () => {
  let service: InboxRealtimeService;

  beforeEach(() => {
    handlers.clear();
    vi.clearAllMocks();
    TestBed.configureTestingModule({});
    service = TestBed.inject(InboxRealtimeService);
  });

  it('connects to the inbox hub with automatic reconnect', () => {
    service.connect();

    expect(withUrl).toHaveBeenCalledWith('/hubs/inbox');
    expect(withAutomaticReconnect).toHaveBeenCalled();
    expect(fakeConnection.start).toHaveBeenCalled();
  });

  it('emits messageStored notifications received from the hub', async () => {
    service.connect();
    const received = firstValueFrom(service.messageStored$);

    handlers.get('messageStored')?.({ conversationId: 'conv-1' });

    expect(await received).toEqual({ conversationId: 'conv-1' });
  });

  it('does not build a second connection on repeated connect calls', () => {
    service.connect();
    service.connect();

    expect(build).toHaveBeenCalledTimes(1);
  });
});
