import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { DatePipe, JsonPipe } from '@angular/common';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { messageOf } from '../core/http-error';
import { IJournalBody, ISessionRequest, SessionApi } from './session-api';

@Component({
  selector: 'app-request-journal',
  imports: [ReactiveFormsModule, DatePipe, JsonPipe],
  templateUrl: './request-journal.html',
  styleUrl: './request-journal.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RequestJournal {
  readonly sessionId = input.required<string>();
  readonly disabled = input(false);
  readonly refreshVersion = input(0);
  readonly busyChanged = output<boolean>();
  readonly rows = signal<ISessionRequest[]>([]);
  readonly selected = signal<Set<number>>(new Set());
  readonly loading = signal(false);
  readonly deleting = signal(false);
  readonly error = signal('');
  readonly notice = signal('');
  readonly hasMore = signal(false);
  readonly detail = signal<ISessionRequest | null>(null);
  readonly detailLoading = signal<number | null>(null);
  readonly detailError = signal('');
  readonly requestContent = computed(() => this.formatBody(this.detail()?.details?.requestBody));
  readonly responseContent = computed(() => this.formatBody(this.detail()?.details?.responseBody));
  readonly allSelected = computed(
    () => this.rows().length > 0 && this.rows().every((row) => this.selected().has(row.id)),
  );
  readonly filters = new FormGroup({
    search: new FormControl('', { nonNullable: true }),
    method: new FormControl('', { nonNullable: true }),
    statusCode: new FormControl<number | null>(null),
    source: new FormControl('', { nonNullable: true }),
    ruleId: new FormControl('', { nonNullable: true }),
    from: new FormControl('', { nonNullable: true }),
    to: new FormControl('', { nonNullable: true }),
  });
  private readonly api = inject(SessionApi);
  private activeFilters: Record<string, string | number> = {};
  private generation = 0;
  private detailGeneration = 0;
  private readonly pageSize = 50;

  private formatBody(body: IJournalBody | null | undefined): string {
    if (!body?.content) return 'Тело отсутствует или не сохранено';
    if (body.encoding === 'json' || body.encoding === 'multipart') {
      try {
        return JSON.stringify(JSON.parse(body.content), null, 2);
      } catch {
        return body.content;
      }
    }
    return body.content;
  }

  constructor() {
    effect(() => {
      const id = this.sessionId();
      this.refreshVersion();
      this.filters.reset();
      this.activeFilters = {};
      untracked(() => void this.load(false, id));
    });
    effect(() => this.busyChanged.emit(this.deleting()));
  }

  async apply(): Promise<void> {
    const values = this.filters.getRawValue();
    if (values.from && values.to && values.from > values.to) {
      this.error.set('Начало периода должно быть раньше конца');
      return;
    }
    this.activeFilters = {};
    for (const [key, value] of Object.entries(values)) {
      const text = String(value ?? '').trim();
      if (!text) continue;
      this.activeFilters[key] =
        key === 'from' || key === 'to' ? new Date(text).toISOString() : text;
    }
    await this.load();
  }

  async resetFilters(): Promise<void> {
    this.filters.reset();
    this.activeFilters = {};
    await this.load();
  }

  async load(append = false, id = this.sessionId()): Promise<void> {
    if (this.deleting()) return;
    const version = ++this.generation;
    this.loading.set(true);
    this.error.set('');
    this.notice.set('');
    if (!append) {
      this.rows.set([]);
      this.selected.set(new Set());
      this.hasMore.set(false);
      this.closeDetails();
    }
    const params: Record<string, string | number> = {
      ...this.activeFilters,
      limit: this.pageSize + 1,
    };
    if (append && this.rows().length) params['beforeId'] = this.rows().at(-1)!.id;
    try {
      const rows = await firstValueFrom(this.api.requests(id, params));
      if (version !== this.generation || id !== this.sessionId()) return;
      this.hasMore.set(rows.length > this.pageSize);
      this.rows.update((current) => [...current, ...rows.slice(0, this.pageSize)]);
    } catch (error: unknown) {
      if (version === this.generation) this.error.set(messageOf(error));
    } finally {
      if (version === this.generation) this.loading.set(false);
    }
  }

  toggle(id: number): void {
    this.selected.update((current) => {
      const next = new Set(current);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  toggleAll(): void {
    this.selected.set(this.allSelected() ? new Set() : new Set(this.rows().map((row) => row.id)));
  }

  async openDetails(id: number): Promise<void> {
    const version = ++this.detailGeneration;
    const sessionId = this.sessionId();
    this.detail.set(null);
    this.detailLoading.set(id);
    this.detailError.set('');
    try {
      const row = await firstValueFrom(this.api.request(sessionId, id));
      if (version === this.detailGeneration && sessionId === this.sessionId()) this.detail.set(row);
    } catch (error: unknown) {
      if (version === this.detailGeneration) this.detailError.set(messageOf(error));
    } finally {
      if (version === this.detailGeneration) this.detailLoading.set(null);
    }
  }

  closeDetails(): void {
    this.detailGeneration++;
    this.detail.set(null);
    this.detailLoading.set(null);
    this.detailError.set('');
  }

  async remove(all: boolean): Promise<void> {
    if (this.deleting() || this.disabled() || (!all && !this.selected().size)) return;
    const message = all
      ? 'Удалить весь журнал этой сессии, включая записи вне фильтров? Состояние и правила сохранятся.'
      : `Удалить выбранные записи (${this.selected().size})?`;
    if (!window.confirm(message)) return;
    this.deleting.set(true);
    this.error.set('');
    const id = this.sessionId();
    let deleted = 0;
    try {
      if (all) deleted = (await firstValueFrom(this.api.deleteRequests(id, { all: true }))).deleted;
      else {
        const ids = [...this.selected()];
        for (let offset = 0; offset < ids.length; offset += 5000)
          deleted += (
            await firstValueFrom(
              this.api.deleteRequests(id, { ids: ids.slice(offset, offset + 5000) }),
            )
          ).deleted;
      }
      this.deleting.set(false);
      await this.load();
      this.notice.set(`Удалено записей: ${deleted}`);
    } catch (error: unknown) {
      this.error.set(messageOf(error));
      this.selected.set(new Set());
      this.closeDetails();
    } finally {
      this.deleting.set(false);
    }
  }
}
