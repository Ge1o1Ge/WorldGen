// Playback is a bounded pull protocol. The server never owns an autonomous
// runner: after each batch the still-open client explicitly asks for the next.
export class SimulationPlayback {
  constructor({advance,onChange=()=>{},onError=()=>{},delay=80,cyclesPerBatch=10,
    setTimer=(callback,ms)=>setTimeout(callback,ms),clearTimer=id=>clearTimeout(id)}) {
    Object.assign(this,{advance,onChange,onError,delay,cyclesPerBatch,setTimer,clearTimer});
    this.playing=false;this.busy=false;this.timer=null;this.speed=1;this.controller=null;this.closed=false;
  }
  clear(){if(this.timer!==null)this.clearTimer(this.timer);this.timer=null;}
  pause(){this.playing=false;this.clear();this.onChange();}
  close(){this.closed=true;this.playing=false;this.clear();this.controller?.abort();this.onChange();}
  setSpeed(speed){if(![1,7,30].includes(speed))throw new RangeError("speed must be 1, 7 or 30");this.speed=speed;this.onChange();}
  start(){if(this.playing||this.closed)return;this.playing=true;this.onChange();if(!this.busy)void this.runBatch();}
  toggle(){if(this.playing)this.pause();else this.start();}
  async runBatch(cycles=this.cyclesPerBatch){
    if(this.busy)return;
    this.clear();this.busy=true;this.controller=new AbortController();this.onChange();
    try{await this.advance(this.speed,cycles,this.controller.signal);}
    catch(error){this.playing=false;if(!(this.closed&&this.controller.signal.aborted))this.onError(error);}
    finally{
      this.busy=false;this.controller=null;this.onChange();
      if(this.playing&&!this.closed){
        try{this.timer=this.setTimer(()=>{this.timer=null;void this.runBatch();},this.delay);}
        catch(error){this.playing=false;this.onError(error);this.onChange();}
      }
    }
  }
}
