// One request in flight; timers only pace playback, never calculate world time.
// Visibility/focus changes are deliberately not a pause command.
export class SimulationPlayback {
  constructor({advance,onChange=()=>{},onError=()=>{},delay=700,
    setTimer=(callback,ms)=>setTimeout(callback,ms),clearTimer=id=>clearTimeout(id)}) {
    Object.assign(this,{advance,onChange,onError,delay,setTimer,clearTimer});
    this.playing=false;this.busy=false;this.timer=null;
  }
  clear(){if(this.timer!==null)this.clearTimer(this.timer);this.timer=null;}
  pause(){this.playing=false;this.clear();this.onChange();}
  start(){if(this.playing)return;this.playing=true;this.onChange();if(!this.busy)void this.step(1);}
  toggle(){if(this.playing)this.pause();else this.start();}
  async step(days=1){
    if(this.busy)return;
    this.clear();this.busy=true;this.onChange();
    try{await this.advance(days);}
    catch(error){this.playing=false;this.onError(error);}
    finally{
      this.busy=false;this.onChange();
      if(this.playing){
        try{this.timer=this.setTimer(()=>{this.timer=null;void this.step(1);},this.delay);}
        catch(error){this.playing=false;this.onError(error);this.onChange();}
      }
    }
  }
}
