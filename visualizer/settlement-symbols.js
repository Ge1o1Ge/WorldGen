export const buildingNames={house:"Жильё",garden:"Освоенный участок",well:"Колодец",warehouse:"Склад",granary:"Амбар",forester_lodge:"Дом лесничего",quarry:"Каменоломня",camp:"Общий очаг",ruin:"Руины",worksite:"Предприятие",water_mill:"Водяная мельница",windmill:"Ветряная мельница",animal_mill:"Мельница с животным приводом",hill_fort:"Укрепление",market_hall:"Рынок",meeting_hall:"Общий дом"};
export const buildingStates={active:"действует",building:"строится",abandoned:"заброшено",demolishing:"разбирается",demolished:"снесено"};
export function buildingGlyph(building){
  if(building.status==="abandoned"||building.status==="demolishing")return "ruin";
  if(building.status==="building")return "construction";
  const kind=building.buildingTypeId??building.kind;
  if(kind==="garden")return "field";
  if(kind==="forester_lodge"||kind==="quarry")return kind;
  return ["house","well","camp","ruin"].includes(kind)?kind:kind?.includes("mill")?"mill":kind?.includes("fort")?"fort":"building";
}
// Four subcell anchors stay in world space, never in screen collision buckets.
export function buildingAnchor(building){
  if((building.buildingTypeId??building.kind)==="garden")return building;
  if(!Number.isInteger(building.slot)||building.slot<0)return building;
  return {...building,x:building.x+(building.slot%2?1:-1)*.23,y:building.y+(Math.floor(building.slot/2)%2?1:-1)*.23};
}
