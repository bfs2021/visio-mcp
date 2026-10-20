export interface EditorExportOptions {
 expected:{documentId:string;sessionId:string;baseRevision:number};
 format:'png'|'jpeg'|'svg'|'pdf';scope:'page'|'selection'|'all';pageId:string;
 elementIds:string[];scale:number;background:'page'|'white'|'transparent';copy?:boolean;
}
