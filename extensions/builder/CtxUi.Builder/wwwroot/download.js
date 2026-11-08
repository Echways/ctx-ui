export async function download(fileName, stream) {
    const url = URL.createObjectURL(new Blob([await stream.arrayBuffer()]));
    const link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    document.body.append(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
}
