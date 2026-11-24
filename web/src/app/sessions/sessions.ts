import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { messageOf } from '../core/http-error';
import { IUnsavedEditor } from '../core/unsaved-guard';
import { IHttpSession, ISessionRequest, SessionApi } from './session-api';

@Component({
  selector: 'app-sessions',
  imports: [ReactiveFormsModule],
  templateUrl: './sessions.html',
  styleUrl: './sessions.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(window:beforeunload)': 'beforeUnload($event)' },
})
export class Sessions implements IUnsavedEditor {
  readonly items = signal<IHttpSession[]>([]);
  readonly selected = signal<IHttpSession | null>(null);
  readonly requests = signal<ISessionRequest[]>([]);
  readonly editing = signal(false);
  readonly busy = signal(false);
  readonly logsLoading = signal(false);
  readonly loading = signal(false);
  readonly error = signal('');
  readonly listError = signal('');
  readonly logsError = signal('');
  readonly notice = signal('');
  readonly config = new FormControl('', { nonNullable: true });
  readonly endpoint = computed(() =>
    this.selected() ? `${location.origin}/s/${this.selected()!.id}` : '',
  );
  readonly mcpEndpoint = `${location.origin}/mcp`;
  private readonly api = inject(SessionApi);
  private savedConfig = '';
  private logRequest = 0;

  constructor() {
    void this.loadList();
  }

  canLeave(): boolean {
    return (
      !this.busy() &&
      (!this.config.dirty ||
        window.confirm('В сессии есть несохранённые изменения. Покинуть редактор?'))
    );
  }
  beforeUnload(event: BeforeUnloadEvent): void {
    if (this.config.dirty || this.busy()) {
      event.preventDefault();
      event.returnValue = '';
    }
  }

  async loadList(): Promise<void> {
    this.loading.set(true);
    this.listError.set('');
    try {
      this.items.set(await firstValueFrom(this.api.list()));
    } catch (error: unknown) {
      this.listError.set(messageOf(error));
    } finally {
      this.loading.set(false);
    }
  }

  create(): void {
    if (!this.canLeave()) return;
    this.selected.set(null);
    this.setEditor({
      name: '',
      upstreamBaseUrl: '',
      proxyEnabled: true,
      initialState: 'started',
      protectedPaths: [],
      rules: [],
    });
  }

  async select(id: string): Promise<void> {
    if (!this.canLeave()) return;
    await this.perform(async () => {
      const session = await firstValueFrom(this.api.get(id));
      this.selected.set(session);
      this.setEditor(session);
      await this.loadRequests();
    });
  }

  async save(): Promise<void> {
    await this.perform(async () => {
      const session = await firstValueFrom(
        this.api.save(this.selected()?.id ?? null, this.parse(this.config.value)),
      );
      this.selected.set(session);
      this.setEditor(session);
      this.notice.set('Сессия сохранена');
      await this.loadList();
      await this.loadRequests();
    });
  }

  discard(): void {
    this.config.reset(this.savedConfig);
    this.error.set('');
    this.notice.set('Изменения сброшены');
  }

  async remove(): Promise<void> {
    const session = this.selected();
    if (!session || !window.confirm('Удалить сессию вместе с правилами и журналом?')) return;
    await this.perform(async () => {
      await firstValueFrom(this.api.remove(session.id));
      this.selected.set(null);
      this.config.reset('');
      this.editing.set(false);
      this.clearRequests();
      this.notice.set('Сессия удалена');
      await this.loadList();
    });
  }

  async reset(): Promise<void> {
    const session = this.selected();
    if (!session || !this.canLeave()) return;
    await this.perform(async () => {
      const saved = await firstValueFrom(this.api.reset(session.id));
      this.selected.set(saved);
      this.setEditor(saved);
      this.notice.set('Состояние и журнал сброшены');
    });
  }

  async loadRequests(): Promise<void> {
    const id = this.selected()?.id;
    if (!id) return;
    const generation = ++this.logRequest;
    this.logsLoading.set(true);
    this.logsError.set('');
    try {
      const requests = await firstValueFrom(this.api.requests(id));
      if (generation === this.logRequest) this.requests.set(requests);
    } catch (error: unknown) {
      if (generation === this.logRequest) this.logsError.set(messageOf(error));
    } finally {
      if (generation === this.logRequest) this.logsLoading.set(false);
    }
  }

  async export(): Promise<void> {
    const id = this.selected()?.id;
    if (!id || !this.canLeave()) return;
    await this.perform(async () => {
      const configuration = await firstValueFrom(this.api.export(id));
      const url = URL.createObjectURL(
        new Blob([JSON.stringify(configuration, null, 2)], { type: 'application/json' }),
      );
      const link = document.createElement('a');
      link.href = url;
      link.download = `session-${id}.json`;
      link.click();
      URL.revokeObjectURL(url);
      this.notice.set('Конфигурация экспортирована');
    });
  }

  async import(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;
    if (!this.canLeave()) {
      input.value = '';
      return;
    }
    await this.perform(async () => {
      if (file.size > 524288) throw new Error('Размер конфигурации превышает 512 KiB');
      const session = await firstValueFrom(this.api.import(this.parse(await file.text())));
      this.selected.set(session);
      this.setEditor(session);
      this.notice.set('Сессия импортирована');
      await this.loadList();
    });
    input.value = '';
  }

  private setEditor(configuration: object): void {
    this.savedConfig = JSON.stringify(configuration, null, 2);
    this.config.reset(this.savedConfig);
    this.editing.set(true);
    this.error.set('');
    this.notice.set('');
    this.clearRequests();
  }
  private clearRequests(): void {
    this.logRequest++;
    this.requests.set([]);
    this.logsError.set('');
    this.logsLoading.set(false);
  }
  private parse(text: string): object {
    let value: unknown;
    try {
      value = JSON.parse(text);
    } catch {
      throw new Error('Некорректный JSON. Проверьте конфигурацию.');
    }
    if (typeof value !== 'object' || value === null || Array.isArray(value))
      throw new Error('Конфигурация должна быть JSON-объектом.');
    return value;
  }
  private async perform(operation: () => Promise<void>): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set('');
    this.notice.set('');
    try {
      await operation();
    } catch (error: unknown) {
      this.error.set(
        error instanceof Error && !(error instanceof HttpErrorResponse)
          ? error.message
          : messageOf(error),
      );
    } finally {
      this.busy.set(false);
    }
  }
}
