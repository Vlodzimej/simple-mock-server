import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  templateUrl: './app.html',
  styleUrl: './app.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {
  readonly dark = signal(localStorage.getItem('mock-theme') === 'dark');
  constructor() {
    this.applyTheme();
  }
  toggleTheme(): void {
    this.dark.update((value) => !value);
    this.applyTheme();
  }
  private applyTheme(): void {
    document.documentElement.dataset['theme'] = this.dark() ? 'dark' : 'light';
    localStorage.setItem('mock-theme', this.dark() ? 'dark' : 'light');
  }
}
