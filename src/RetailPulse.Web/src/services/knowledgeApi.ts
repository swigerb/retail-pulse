import type { KBDocument, KBSearchResult, KBStats } from '../types';

export async function fetchDocuments(): Promise<KBDocument[]> {
  const res = await fetch('/api/knowledge/documents');
  if (!res.ok) throw new Error(`Failed to fetch documents: ${res.status}`);
  return res.json();
}

export async function uploadDocument(file: File, title: string): Promise<KBDocument> {
  const formData = new FormData();
  formData.append('file', file);
  formData.append('title', title);
  const res = await fetch('/api/knowledge/documents', {
    method: 'POST',
    body: formData,
  });
  if (!res.ok) throw new Error(`Failed to upload document: ${res.status}`);
  return res.json();
}

export async function deleteDocument(id: string): Promise<void> {
  const res = await fetch(`/api/knowledge/documents/${id}`, { method: 'DELETE' });
  if (!res.ok) throw new Error(`Failed to delete document: ${res.status}`);
}

export async function searchKnowledgeBase(query: string): Promise<KBSearchResult[]> {
  const res = await fetch(`/api/knowledge/search?q=${encodeURIComponent(query)}`);
  if (!res.ok) throw new Error(`Failed to search KB: ${res.status}`);
  return res.json();
}

export async function fetchKBStats(): Promise<KBStats> {
  const res = await fetch('/api/knowledge/stats');
  if (!res.ok) throw new Error(`Failed to fetch KB stats: ${res.status}`);
  return res.json();
}
