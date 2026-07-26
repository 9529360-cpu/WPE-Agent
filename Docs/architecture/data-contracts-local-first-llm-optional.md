# Data Contracts

## 1. Task
```ts
type Task = {
  id: string;
  kind: 'deterministic' | 'analysis' | 'generation' | 'research';
  priority: 'low' | 'normal' | 'high';
  privacyMode: boolean;
  offlineMode: boolean;
  budget: {
    maxTokens: number;
    maxCostUsd: number;
  };
};
```

## 2. RouteDecision
```ts
type RouteDecision = {
  route: 'local' | 'brain' | 'fallback';
  reason: string;
  confidence: number;
  estimatedCostUsd: number;
  cacheHit: boolean;
};
```

## 3. BrainRequest
```ts
type BrainRequest = {
  provider: string;
  model: string;
  prompt: string;
  contextSummary?: string;
  maxTokens: number;
  timeoutMs: number;
  privacyMode: boolean;
};
```

## 4. BudgetState
```ts
type BudgetState = {
  periodId: string;
  tokensUsed: number;
  costUsdUsed: number;
  hardStop: boolean;
  softWarn: boolean;
};
```

## 5. MemoryItem
```ts
type MemoryItem = {
  key: string;
  scope: 'session' | 'project' | 'user';
  summary: string;
  sourceTaskId: string;
  ttlSeconds: number;
};
```

## 6. Event
- `route.selected`
- `brain.requested`
- `brain.failed`
- `fallback.used`
- `budget.exceeded`
- `privacy.blocked`
- `offline.enabled`
