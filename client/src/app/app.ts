import { Component, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HealthAssistantApiService } from './health-assistant-api.service';

type ConversationMessage = {
  id: string;
  role: 'user' | 'assistant';
  content: string;
};

@Component({
  selector: 'app-root',
  imports: [FormsModule],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App {
  protected readonly messages = signal<ConversationMessage[]>([]);
  protected readonly isSending = signal(false);
  protected readonly errorMessage = signal('');
  protected draft = '';

  constructor(private readonly api: HealthAssistantApiService) {}

  protected async sendMessage(suggestedMessage?: string): Promise<void> {
    const message = (suggestedMessage ?? this.draft).trim();
    if (!message || this.isSending()) {
      return;
    }

    this.errorMessage.set('');
    this.draft = '';
    const previousMessages = this.messages().map(({ role, content }) => ({ role, content }));
    this.messages.update((current) => [...current, { id: crypto.randomUUID(), role: 'user', content: message }]);
    this.isSending.set(true);
    const assistantId = crypto.randomUUID();
    this.messages.update((current) => [...current, { id: assistantId, role: 'assistant', content: '' }]);

    try {
      await this.api.chat(
        {
          message,
          history: previousMessages,
        },
        (chunk) =>
          this.messages.update((current) =>
            current.map((item) =>
              item.id === assistantId ? { ...item, content: item.content + chunk } : item,
            ),
          ),
      );
    } catch (error: unknown) {
      this.errorMessage.set(this.getErrorMessage(error));
      this.messages.update((current) =>
        current.filter((item) => item.id !== assistantId || item.content.length > 0),
      );
    } finally {
      this.isSending.set(false);
    }
  }

  protected askSuggestion(message: string): void {
    void this.sendMessage(message);
  }

  protected startNewChat(): void {
    this.messages.set([]);
    this.errorMessage.set('');
    this.draft = '';
  }

  protected onComposerKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      void this.sendMessage();
    }
  }

  private getErrorMessage(error: unknown): string {
    if (error instanceof TypeError) {
      return 'Could not reach the API. Check that the .NET server is running.';
    }
    if (error instanceof Error) {
      return error.message;
    }
    return 'Something went wrong. Please try again.';
  }
}
