import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';

export interface IHttpSession {
  id: string;
  name: string;
  state: string;
  upstreamBaseUrl: string;
  rules: unknown[];
}

export interface ISessionRequest {
  id: number;
  createdAt: string;
  method: string;
  path: string;
  ruleId: string | null;
  source: string;
  statusCode: number;
  durationMs: number;
  reason: string;
  queryString: string | null;
  details?: ISessionRequestDetails;
}

export interface IJournalBody {
  content: string;
  encoding: string;
  contentType: string | null;
  size: number | null;
  truncated: boolean;
  note: string | null;
}

export interface ISessionRequestDetails {
  requestUrl: string;
  upstreamUrl: string | null;
  protocol: string;
  traceId: string;
  remoteAddress: string | null;
  remotePort: number;
  localAddress: string | null;
  localPort: number;
  stateBefore: string;
  stateAfter: string;
  completedAt: string;
  clientAborted: boolean;
  requestHeaders: Record<string, string[]>;
  responseHeaders: Record<string, string[]>;
  requestBody: IJournalBody | null;
  responseBody: IJournalBody | null;
}

@Injectable({ providedIn: 'root' })
export class SessionApi {
  private readonly http = inject(HttpClient);
  private readonly path = '/admin/api/sessions';

  list() {
    return this.http.get<IHttpSession[]>(this.path);
  }
  get(id: string) {
    return this.http.get<IHttpSession>(`${this.path}/${id}`);
  }
  save(id: string | null, configuration: object) {
    return id
      ? this.http.put<IHttpSession>(`${this.path}/${id}`, configuration)
      : this.http.post<IHttpSession>(this.path, configuration);
  }
  remove(id: string) {
    return this.http.delete<void>(`${this.path}/${id}`);
  }
  reset(id: string) {
    return this.http.post<IHttpSession>(`${this.path}/${id}/reset`, null);
  }
  export(id: string) {
    return this.http.get<object>(`${this.path}/${id}/export`);
  }
  import(configuration: object) {
    return this.http.post<IHttpSession>(`${this.path}/import`, configuration);
  }
  requests(id: string, params: Record<string, string | number> = {}) {
    return this.http.get<ISessionRequest[]>(`${this.path}/${id}/requests`, { params });
  }
  request(id: string, requestId: number) {
    return this.http.get<ISessionRequest>(`${this.path}/${id}/requests/${requestId}`);
  }
  deleteRequests(id: string, body: { all: true } | { ids: number[] }) {
    return this.http.delete<{ deleted: number }>(`${this.path}/${id}/requests`, { body });
  }
}
