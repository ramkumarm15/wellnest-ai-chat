import { afterEach, describe, expect, it, vi } from 'vitest';
import { HealthAssistantApiService } from './health-assistant-api.service';

describe('HealthAssistantApiService', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('streams response chunks and correctly decodes split UTF-8 characters', async () => {
    const encodedAnswer = new TextEncoder().encode('Hello 🌿');
    const splitAt = encodedAnswer.length - 2;
    const responseBody = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(encodedAnswer.slice(0, splitAt));
        controller.enqueue(encodedAnswer.slice(splitAt));
        controller.close();
      },
    });
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(responseBody)));
    const chunks: string[] = [];

    await new HealthAssistantApiService().chat(
      { message: 'question', history: [] },
      (chunk) => chunks.push(chunk),
    );

    expect(chunks.join('')).toBe('Hello 🌿');
  });
});
