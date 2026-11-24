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
  method: string;
  path: string;
  ruleId: string | null;
  source: string;
  statusCode: number;
  durationMs: number;
  reason: string;
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
  requests(id: string) {
    return this.http.get<ISessionRequest[]>(`${this.path}/${id}/requests`);
  }
}
