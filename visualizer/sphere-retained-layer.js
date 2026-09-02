export function retainedCommitKeys(baseKeys,desired,{incremental=false,replacesBase=false}={}){
  const exact=[...new Set(desired)];
  // A same-key version change cannot stay in a separate patch buffer: the old
  // copy would still be drawn underneath it. During camera motion rebuild the
  // union instead of replacing the complete working set with only the newly
  // sampled viewport. The settled frame trims it afterwards.
  return incremental&&replacesBase?[...new Set([...(baseKeys??[]),...exact])]:exact;
}

export function retainedTransitionNeeded(previousLength,animateReplacement){
  // Visibility/LOD changes are camera work and must appear in the same frame.
  // Cross-fading those buffers makes every newly exposed road and border start
  // fully transparent. Animate only a new version of existing world data.
  return previousLength>0&&animateReplacement;
}

export function partitionRetainedEntries(entries){
  const fixed=[],animated=[];
  for(const entry of entries)(entry.animate?animated:fixed).push(entry);
  return {fixed,animated};
}
