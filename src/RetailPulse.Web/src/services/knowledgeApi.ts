import type { KBDocument, KBSearchResult, KBStats, KnowledgeUploadResponse } from '../types';

export async function fetchDocuments(): Promise<KBDocument[]> {
  const res = await fetch('/api/knowledge/documents');
  if (!res.ok) throw new Error(`Failed to fetch documents: ${res.status}`);
  return res.json();
}

export async function uploadDocument(
  file: File,
  title: string,
  source?: string,
): Promise<KnowledgeUploadResponse> {
  const content = await file.text();
  const res = await fetch('/api/knowledge/upload', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ title, content, source: source ?? 'upload' }),
  });
  if (!res.ok) throw new Error(`Failed to upload document: ${res.status}`);
  return res.json();
}

export async function deleteDocument(id: string): Promise<void> {
  const res = await fetch(`/api/knowledge/documents/${id}`, { method: 'DELETE' });
  if (!res.ok) throw new Error(`Failed to delete document: ${res.status}`);
}

export async function searchKnowledgeBase(
  query: string,
  topK?: number,
): Promise<KBSearchResult[]> {
  const res = await fetch('/api/knowledge/search', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ query, topK }),
  });
  if (!res.ok) throw new Error(`Failed to search KB: ${res.status}`);
  const data: { query: string; results: KBSearchResult[] } = await res.json();
  return data.results;
}

export async function fetchKBStats(): Promise<KBStats> {
  const res = await fetch('/api/knowledge/stats');
  if (!res.ok) throw new Error(`Failed to fetch KB stats: ${res.status}`);
  return res.json();
}
