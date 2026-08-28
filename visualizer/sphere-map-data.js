import { facePoint, locateFace } from "./sphere-cartography.js";

// Immutable terrain chunks + a small, cumulative simulation overlay.
// A missed day is harmless: forest is a replacement snapshot, not a chain of deltas.
export class SphereMapData {
  constructor({worldId,faceSize,chunkSize,faces}) {
    this.worldId=worldId;this.faceSize=faceSize;this.chunkSize=chunkSize;
    this.faces=new Map(faces.map((face,index)=>[face,index]));
    this.chunkAxis=Math.ceil(faceSize/chunkSize);
    this.revision=-1;this.claimsRevision=-1;this.forest=new Map();this.claims=new Map();
    this.tileVersions=new Map();this.dependencies=new Map();this.ready=false;this.updates=0;
  }
  key(cell){return (this.faces.get(cell.face)*this.faceSize+cell.y)*this.faceSize+cell.x;}
  tile(cell){return this.faces.get(cell.face)*this.chunkAxis*this.chunkAxis+Math.floor(cell.y/this.chunkSize)*this.chunkAxis+Math.floor(cell.x/this.chunkSize);}
  tileForKey(key){const x=key%this.faceSize,y=Math.floor(key/this.faceSize)%this.faceSize,face=Math.floor(key/(this.faceSize*this.faceSize));return face*this.chunkAxis*this.chunkAxis+Math.floor(y/this.chunkSize)*this.chunkAxis+Math.floor(x/this.chunkSize);}
  version(tile){return this.tileVersions.get(tile)??0;}
  dependenciesFor({face,tx,ty}){
    const own=this.tile({face,x:tx*this.chunkSize,y:ty*this.chunkSize});
    if(!this.dependencies.has(own)){
      const keys=new Set(),offsets=[-9,this.chunkSize/2,this.chunkSize+9];
      // Cover the two-node contour halo (up to 8 zones), including cube seams.
      for(const dy of offsets)for(const dx of offsets){
        const location=locateFace(facePoint(face,tx*this.chunkSize+dx,ty*this.chunkSize+dy,this.faceSize));
        keys.add(this.tile({face:location.face,
          x:Math.max(0,Math.min(this.faceSize-1,Math.floor((location.u+1)*this.faceSize/2))),
          y:Math.max(0,Math.min(this.faceSize-1,Math.floor((location.v+1)*this.faceSize/2)))}));
      }
      this.dependencies.set(own,[...keys]);
    }
    return this.dependencies.get(own);
  }
  apply(update) {
    if(update.worldId!==this.worldId)throw new Error("Мир на сервере заменён. Обновите страницу; текущая карта сохранена до перезагрузки.");
    if(update.revision<this.revision)return {accepted:false,changedTiles:new Set(),structures:null};
    if(update.claimsRevision!==this.claimsRevision&&(!update.claims||!update.settlements))throw new Error("Не получен полный слой границ. Повторите синхронизацию.");
    const changedTiles=new Set(),forest=new Map(update.forest.map(cell=>[this.key(cell),cell.forest]));
    for(const key of new Set([...forest.keys(),...this.forest.keys()]))if(forest.get(key)!==this.forest.get(key))changedTiles.add(this.tileForKey(key));
    if(update.claims){
      const claims=new Map(update.claims.map(cell=>[this.key(cell),{owner:cell.owner,influence:cell.influence}]));
      for(const key of new Set([...claims.keys(),...this.claims.keys()])){
        const a=claims.get(key),b=this.claims.get(key);
        if(a?.owner!==b?.owner||a?.influence!==b?.influence)changedTiles.add(this.tileForKey(key));
      }
      this.claims=claims;
    }
    this.forest=forest;this.revision=update.revision;this.claimsRevision=update.claimsRevision;this.ready=true;
    this.markTiles(changedTiles);this.updates++;
    return {accepted:true,changedTiles,structures:update.settlements};
  }
  markTiles(tiles){for(const tile of tiles)this.tileVersions.set(tile,this.version(tile)+1);}
  read(cell,base){
    if(!this.ready)return base;
    const key=this.key(cell),claim=this.claims.get(key);
    return {...base,forest:this.forest.get(key)??base.forest,owner:claim?.owner??-1,influence:claim?.influence??0};
  }
  query(){return `mapWorldId=${encodeURIComponent(this.worldId)}&mapClaimsRevision=${this.claimsRevision}`;}
}

// Ignore resident counts and construction progress: they are UI state, not geometry.
export function structureTiles(before,after,mapData){
  const entries=settlements=>new Map(settlements.flatMap(city=>[
    ...city.buildings.map(b=>[`${city.id}:b:${b.id}:${b.face}:${b.x}:${b.y}`,{cell:b,stamp:`${b.buildingTypeId}:${b.capacityUnits}`}]),
    ...city.usedLands.map(l=>[`${city.id}:l:${l.id}`,{cell:l,stamp:`${l.face}:${l.x}:${l.y}:${l.usage>0}`}])
  ]));
  const a=entries(before),b=entries(after),tiles=new Set();
  for(const key of new Set([...a.keys(),...b.keys()]))if(a.get(key)?.stamp!==b.get(key)?.stamp){
    if(a.has(key))tiles.add(mapData.tile(a.get(key).cell));if(b.has(key))tiles.add(mapData.tile(b.get(key).cell));
  }
  return tiles;
}
