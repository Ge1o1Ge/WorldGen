export function interpolateWorldDay(fromDay,targetDay,elapsedMs,durationMs){
  if(!Number.isFinite(fromDay)||!Number.isFinite(targetDay))return targetDay;
  if(!(durationMs>0))return targetDay;
  const t=Math.max(0,Math.min(1,elapsedMs/durationMs));
  return fromDay+(targetDay-fromDay)*t;
}

export function calendarPosition(displayDay,yearDays=360,monthDays=30){
  const safe=Number.isFinite(displayDay)?Math.max(0,displayDay):0;
  const whole=Math.floor(safe+1e-7),yearDay=((safe%yearDays)+yearDays)%yearDays;
  const wholeYearDay=((whole%yearDays)+yearDays)%yearDays;
  return{whole,yearDay,year:Math.floor(whole/yearDays)+1,month:Math.floor(wholeYearDay/monthDays)+1,dayOfMonth:wholeYearDay%monthDays+1};
}
