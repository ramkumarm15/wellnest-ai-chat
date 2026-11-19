import { Injectable } from '@angular/core';

type ConversationTurn = {
  role: 'user' | 'assistant';
  content: string;
};

type ChatRequest = {
  message: string;
  history: ConversationTurn[];
};

@Injectable({ providedIn: 'root' })
export class HealthAssistantApiService {
  private readonly apiUrl = 'http://localhost:5124/api';

  async chat(request: ChatRequest, onChunk: (chunk: string) => void): Promise<void> {
    const response = await fetch(`${this.apiUrl}/chat`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    });

    if (!response.ok) {
      const errorBody: unknown = await response.json();
      const detail =
        typeof errorBody === 'object' &&
        errorBody !== null &&
        'detail' in errorBody &&
        typeof errorBody.detail === 'string'
          ? errorBody.detail
          : `The request failed (${response.status}). Please try again.`;
      throw new Error(detail);
    }

    if (!response.body) {
      throw new Error('The API did not return a response stream.');
    }

    const reader = response.body.getReader();
    const decoder = new TextDecoder();

    try {
      while (true) {
        const { done, value } = await reader.read();
        if (done) {
          break;
        }

        const chunk = decoder.decode(value, { stream: true });
        if (chunk) {
          onChunk(chunk);
        }
      }

      const finalChunk = decoder.decode();
      if (finalChunk) {
        onChunk(finalChunk);
      }
    } finally {
      reader.releaseLock();
    }
  }
}
