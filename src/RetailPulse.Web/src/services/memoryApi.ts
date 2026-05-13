import type { MemoryEntry } from '../types';

export async function fetchMemories(): Promise<MemoryEntry[]> {
  const res = await fetch('/api/memory');
  if (!res.ok) throw new Error(`Failed to fetch memories: ${res.status}`);
  return res.json();
}

export async function deleteMemory(id: string): Promise<void> {
  const res = await fetch(`/api/memory/${encodeURIComponent(id)}`, { method: 'DELETE' });
  if (!res.ok) throw new Error(`Failed to delete memory: ${res.status}`);
}

export async function deleteAllMemories(): Promise<void> {
  const res = await fetch('/api/memory', { method: 'DELETE' });
  if (!res.ok) throw new Error(`Failed to delete all memories: ${res.status}`);
}
