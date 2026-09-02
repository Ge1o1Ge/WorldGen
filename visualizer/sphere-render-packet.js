export const RenderPacket={
  version:2,
  opcode:{texture2DArray:1,texture2DArrayPatch:2},
  resource:{terrain:1,water:2,elevation:3},
  format:{rgba8:1,rg8:2,r32f:3},
};

const HEADER_BYTES=16;

export function parseRenderPacket(buffer){
  if(!(buffer instanceof ArrayBuffer))throw new TypeError('Render packet must be an ArrayBuffer');
  const bytes=new Uint8Array(buffer),view=new DataView(buffer);
  if(bytes.byteLength<HEADER_BYTES)throw new Error('Render packet is truncated');
  if(bytes[0]!==87||bytes[1]!==71||bytes[2]!==82||bytes[3]!==80)throw new Error('Render packet has an invalid magic');
  const version=view.getUint16(4,true),descriptorBytes=view.getUint16(6,true),commandCount=view.getUint32(8,true),totalBytes=view.getUint32(12,true);
  if(version!==RenderPacket.version)throw new Error(`Unsupported render packet version: ${version}`);
  if(descriptorBytes<32)throw new Error(`Render command descriptor is too small: ${descriptorBytes}`);
  if(totalBytes!==bytes.byteLength)throw new Error(`Render packet length mismatch: ${bytes.byteLength} != ${totalBytes}`);
  const headerBytes=HEADER_BYTES+descriptorBytes*commandCount;
  if(headerBytes>bytes.byteLength)throw new Error('Render command table is truncated');
  const commands=[];
  for(let index=0;index<commandCount;index++){
    const at=HEADER_BYTES+index*descriptorBytes;
    const offset=view.getUint32(at+12,true),length=view.getUint32(at+16,true);
    if(offset<headerBytes||offset+length>bytes.byteLength||offset+length<offset)throw new Error(`Render command ${index} points outside the packet`);
    commands.push({
      opcode:bytes[at],resource:bytes[at+1],format:bytes[at+2],flags:bytes[at+3],
      width:view.getUint16(at+4,true),height:view.getUint16(at+6,true),layers:view.getUint16(at+8,true),mipLevel:view.getUint16(at+10,true),
      offset,length,revision:view.getUint32(at+20,true),payload:bytes.subarray(offset,offset+length),
      x:view.getUint16(at+24,true),y:view.getUint16(at+26,true),layer:view.getUint16(at+28,true),
    });
  }
  return{version,commands,byteLength:bytes.byteLength};
}
