export const MAX_INTERFACE_LATENCY_MS=450;

export function interfaceWorkCanWait({queuedAt,now,hardBlocked=false,softBlocked=false,maxLatency=MAX_INTERFACE_LATENCY_MS}){
  if(hardBlocked)return true;
  return softBlocked&&now-queuedAt<maxLatency;
}

export function interfaceWorkDelay({queuedAt,now,requested=120,maxLatency=MAX_INTERFACE_LATENCY_MS}){
  return Math.max(0,Math.min(requested,queuedAt+maxLatency-now));
}
